using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Portaballs.Core;
using Valve.VR;

namespace Portaballs.Helper
{
    /// <summary>
    /// Reads head, hands, grip, trigger and eye gaze from a SteamVR that is ALREADY running.
    ///
    /// It never starts SteamVR. Before it even loads SteamVR's library it checks that SteamVR's own
    /// process (vrserver.exe) is alive; if not, it reports "nothing to read" and the poll loop asks
    /// again a quarter of a second later. That check is what makes the choice of application type safe:
    ///
    /// Application type: OVERLAY by default.
    ///   The helper has to keep reading while the GAME is the program in front. In OpenVR, a "scene"
    ///   application only gets input while it has focus; an "overlay" application is the kind that is
    ///   designed to run alongside the scene application and keep its own action set live (openvr.h
    ///   says so where it describes VRActiveActionSet_t priorities for ApplicationType_Overlay).
    ///   A "background" application is the other candidate. Its documented difference is that it does
    ///   not start SteamVR and does not keep it alive; the header says nothing about whether its
    ///   actions stay live while a game has focus, so it is the fallback (--app-type background), to
    ///   be tried with the headset on if overlay misbehaves. An overlay application WOULD start
    ///   SteamVR if asked to initialise without it, hence the vrserver.exe check above.
    ///
    /// What is read, per poll:
    ///   Poses    IVRSystem::GetDeviceToAbsoluteTrackingPose(Standing, 0 seconds ahead): every device's
    ///            pose, velocity and angular velocity "at the instant the method is called". Stamped
    ///            with the Stopwatch time halfway between just before and just after the call.
    ///            This is each device's raw pose (the same thing as the /pose/raw input path).
    ///   Inputs   IVRInput::UpdateActionState, then GetAnalogActionData for grip and trigger on each
    ///            hand. Besides the value, SteamVR gives fUpdateTime: how long ago (a negative number
    ///            of seconds) the value last changed. GripTime = (Stopwatch time right after
    ///            UpdateActionState) + fUpdateTime. That is the moment the controller's reading
    ///            changed, not the moment we happened to poll, which is how the game places a release
    ///            more finely than the 2 ms polling interval: it feeds (GripTime, Grip) pairs, not
    ///            (sample time, Grip) pairs, to the release detector.
    ///   Gaze     IVRInput::GetEyeTrackingDataRelativeToNow(action, Standing, 0 seconds): where the
    ///            gaze ray starts and the point it is aimed at, with active / valid / tracked flags.
    ///
    /// Everything is converted from SteamVR's axes to the core's by OpenVrSpace.
    /// </summary>
    internal sealed class OpenVrSource : ISampleSource
    {
        private const string ActionSet = "/actions/link";
        private const string GripAction = "/actions/link/in/grip";
        private const string TriggerAction = "/actions/link/in/trigger";
        private const string GazeAction = "/actions/link/in/gaze";

        private const int RetryEveryMs = 2000;

        private readonly FileLog _log;
        private readonly EVRApplicationType _appType;
        private readonly string _manifestPath;

        private CVRSystem _system;
        private CVRInput _input;
        private Process _server;
        private bool _connected;
        private bool _waitForServerToExit;
        private long _nextAttemptTicks;
        private string _lastProblem = "";
        private string _detail = "waiting_for_steamvr";

        private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        private readonly VRActiveActionSet_t[] _activeSets = new VRActiveActionSet_t[1];
        private readonly uint _sizeOfActiveSet = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));
        private readonly uint _sizeOfAnalog = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        private readonly uint _sizeOfEye = (uint)Marshal.SizeOf(typeof(VREyeTrackingData_t));
        private readonly uint _sizeOfEvent = (uint)Marshal.SizeOf(typeof(VREvent_t));

        private ulong _setHandle, _gripHandle, _triggerHandle, _gazeHandle, _leftSource, _rightSource;
        private bool _inputReady;
        private uint _leftIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        private uint _rightIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        private long _polls;

        // Things worth saying once, the first time they are seen with the headset on.
        private bool _sawHead, _sawLeft, _sawRight, _sawGrip, _sawTrigger, _sawGazeActive, _sawGazeUsable;
        private EVRInputError _lastUpdateError = EVRInputError.None;
        private EVRInputError _lastAnalogError = EVRInputError.None;
        private EVRInputError _lastGazeError = EVRInputError.None;
        private long _gazeRetryAtPoll;
        private double _updateAgeMin = double.MaxValue, _updateAgeMax = double.MinValue;
        private long _gripChanges;
        private float _lastGripLeft = float.NaN, _lastGripRight = float.NaN;

        public OpenVrSource(FileLog log, string appType, string baseDirectory)
        {
            _log = log;
            _appType = appType == "background" ? EVRApplicationType.VRApplication_Background : EVRApplicationType.VRApplication_Overlay;
            _manifestPath = Path.Combine(baseDirectory, "actions", "actions.json");
        }

        public string Name { get { return "openvr"; } }

        public string Detail { get { return _detail; } }

        public bool TryRead(ref LinkSample sample)
        {
            if (!_connected)
            {
                long now = Clock.Ticks;
                if (now < _nextAttemptTicks) return false;
                _nextAttemptTicks = now + Clock.TicksPerSecond * RetryEveryMs / 1000;
                if (!TryConnect()) return false;
            }

            try
            {
                return ReadOnce(ref sample);
            }
            catch (Exception e)
            {
                Problem("Reading SteamVR failed: " + e.Message);
                Disconnect("read_error", false);
                return false;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Connecting and disconnecting
        // ------------------------------------------------------------------------------------------

        private bool TryConnect()
        {
            Process server = FindServer();
            if (_waitForServerToExit)
            {
                // SteamVR told us it is quitting. Do not touch it again until it has really gone.
                if (server != null)
                {
                    server.Dispose();
                    return false;
                }
                _waitForServerToExit = false;
            }
            if (server == null)
            {
                _detail = "waiting_for_steamvr";
                Problem("SteamVR is not running. Waiting for it (the helper never starts it).");
                return false;
            }

            try
            {
                EVRInitError error = EVRInitError.None;
                CVRSystem system = OpenVR.Init(ref error, _appType);
                if (error != EVRInitError.None || system == null)
                {
                    server.Dispose();
                    _detail = "steamvr_refused_" + error;
                    Problem("SteamVR is running but would not let the helper in: " + error + ".");
                    return false;
                }
                _system = system;
                _input = OpenVR.Input;
                _server = server;
            }
            catch (DllNotFoundException e)
            {
                server.Dispose();
                _detail = "openvr_api_dll_missing";
                Problem("openvr_api.dll could not be loaded from beside the program: " + e.Message);
                return false;
            }
            catch (Exception e)
            {
                server.Dispose();
                _detail = "steamvr_init_error";
                Problem("Connecting to SteamVR failed: " + e.Message);
                return false;
            }

            _connected = true;
            _lastProblem = "";
            _polls = 0;
            _detail = "steamvr_connected";
            _log.Info("Connected to SteamVR as " + _appType + ". Runtime version " + Safe(() => _system.GetRuntimeVersion()) + ".");
            SetUpInput();
            RefreshControllers();
            DescribeDevices();
            return true;
        }

        private static Process FindServer()
        {
            Process[] found = Process.GetProcessesByName("vrserver");
            Process keep = null;
            for (int i = 0; i < found.Length; i++)
            {
                if (keep == null) keep = found[i];
                else found[i].Dispose();
            }
            return keep;
        }

        private void SetUpInput()
        {
            _inputReady = false;
            if (_input == null)
            {
                _log.Warn("SteamVR gave no input interface (" + OpenVR.IVRInput_Version + "). Poses will flow; grip, trigger and gaze will not.");
                return;
            }
            if (!File.Exists(_manifestPath))
            {
                _log.Warn("Action list not found at " + _manifestPath + ". Poses will flow; grip, trigger and gaze will not.");
                return;
            }

            // Must happen before the first UpdateActionState or PollNextEvent (openvr.h, IVRInput).
            EVRInputError e = _input.SetActionManifestPath(_manifestPath);
            if (e != EVRInputError.None)
            {
                _log.Warn("SteamVR did not accept the action list (" + e + "). Poses will flow; grip, trigger and gaze will not.");
                return;
            }
            EVRInputError e1 = _input.GetActionSetHandle(ActionSet, ref _setHandle);
            EVRInputError e2 = _input.GetActionHandle(GripAction, ref _gripHandle);
            EVRInputError e3 = _input.GetActionHandle(TriggerAction, ref _triggerHandle);
            EVRInputError e4 = _input.GetActionHandle(GazeAction, ref _gazeHandle);
            EVRInputError e5 = _input.GetInputSourceHandle("/user/hand/left", ref _leftSource);
            EVRInputError e6 = _input.GetInputSourceHandle("/user/hand/right", ref _rightSource);
            if (e1 != EVRInputError.None || e2 != EVRInputError.None || e3 != EVRInputError.None || e5 != EVRInputError.None || e6 != EVRInputError.None)
            {
                _log.Warn("SteamVR would not hand out the input handles (set " + e1 + ", grip " + e2 + ", trigger " + e3 + ", left " + e5 + ", right " + e6 + ").");
                return;
            }
            if (e4 != EVRInputError.None)
            {
                _gazeHandle = OpenVR.k_ulInvalidActionHandle;
                _log.Warn("No handle for the eye-gaze action (" + e4 + "). Gaze will not flow.");
            }

            _activeSets[0] = new VRActiveActionSet_t();
            _activeSets[0].ulActionSet = _setHandle;
            _activeSets[0].ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle;
            _activeSets[0].nPriority = 0;   // ordinary priority: never takes inputs away from the game
            _inputReady = true;
            _log.Info("Action list accepted: " + _manifestPath);
        }

        private void DescribeDevices()
        {
            _log.Info("Headset: " + DeviceText(OpenVR.k_unTrackedDeviceIndex_Hmd) + ".");
            _log.Info("Left controller: " + DeviceText(_leftIndex) + ". Right controller: " + DeviceText(_rightIndex) + ".");
            try
            {
                ETrackedPropertyError pe = ETrackedPropertyError.TrackedProp_Success;
                bool gazeProp = _system.GetBoolTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_SupportsXrEyeGazeInteraction_Bool, ref pe);
                _log.Info("Headset says it supports eye gaze: " + (pe == ETrackedPropertyError.TrackedProp_Success ? gazeProp.ToString() : "unknown (" + pe + ")") + ".");

                CVRSettings settings = OpenVR.Settings;
                if (settings != null)
                {
                    EVRSettingsError se = EVRSettingsError.None;
                    bool share = settings.GetBool("driver_vrlink", "shareEyeTrackingData", ref se);
                    if (se == EVRSettingsError.None)
                    {
                        _log.Info("SteamVR setting 'Share eye tracking data to other apps on this PC' is " + (share ? "ON." : "OFF. Gaze will stay empty until it is switched on in SteamVR's settings."));
                    }
                    else
                    {
                        _log.Info("Could not read the eye-tracking sharing setting (" + se + ").");
                    }
                }
            }
            catch (Exception e)
            {
                _log.Info("Could not read the eye-tracking details: " + e.Message);
            }
        }

        private string DeviceText(uint index)
        {
            if (index == OpenVR.k_unTrackedDeviceIndexInvalid) return "not found yet";
            return "#" + index.ToString(CultureInfo.InvariantCulture)
                + " model '" + Property(index, ETrackedDeviceProperty.Prop_ModelNumber_String)
                + "' type '" + Property(index, ETrackedDeviceProperty.Prop_ControllerType_String)
                + "' tracking '" + Property(index, ETrackedDeviceProperty.Prop_TrackingSystemName_String) + "'";
        }

        private string Property(uint index, ETrackedDeviceProperty property)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                _system.GetStringTrackedDeviceProperty(index, property, sb, (uint)sb.Capacity, ref error);
                return error == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : "?";
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private void RefreshControllers()
        {
            uint left = _system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            uint right = _system.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
            if (left != _leftIndex || right != _rightIndex)
            {
                _leftIndex = left;
                _rightIndex = right;
                if (_polls > 0) _log.Info("Controllers changed. Left: " + DeviceText(left) + ". Right: " + DeviceText(right) + ".");
            }
        }

        private bool ServerHasExited()
        {
            try
            {
                return _server != null && _server.HasExited;
            }
            catch (Exception)
            {
                return false;   // not allowed to ask; the Quit event and failed reads still cover it
            }
        }

        private void Disconnect(string detail, bool steamVrIsQuitting)
        {
            if (_connected)
            {
                try { OpenVR.Shutdown(); } catch (Exception) { }
            }
            _connected = false;
            _system = null;
            _input = null;
            _inputReady = false;
            _leftIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
            _rightIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
            if (_server != null)
            {
                _server.Dispose();
                _server = null;
            }
            _waitForServerToExit = steamVrIsQuitting;
            _detail = detail;
            _nextAttemptTicks = Clock.Ticks + Clock.TicksPerSecond * RetryEveryMs / 1000;
        }

        // ------------------------------------------------------------------------------------------
        // One poll
        // ------------------------------------------------------------------------------------------

        private bool ReadOnce(ref LinkSample sample)
        {
            _polls++;

            // Housekeeping, spread out so it never lands on every poll.
            if (_polls % 16 == 0)
            {
                VREvent_t ev = new VREvent_t();
                while (_system.PollNextEvent(ref ev, _sizeOfEvent))
                {
                    EVREventType type = (EVREventType)ev.eventType;
                    if (type == EVREventType.VREvent_Quit)
                    {
                        _log.Info("SteamVR is quitting. Letting go of it.");
                        try { _system.AcknowledgeQuit_Exiting(); } catch (Exception) { }
                        Disconnect("steamvr_quit", true);
                        return false;
                    }
                    if (type == EVREventType.VREvent_TrackedDeviceActivated || type == EVREventType.VREvent_TrackedDeviceDeactivated
                        || type == EVREventType.VREvent_TrackedDeviceRoleChanged || type == EVREventType.VREvent_TrackedDeviceUpdated)
                    {
                        RefreshControllers();
                    }
                }
            }
            if (_polls % 512 == 0)
            {
                if (ServerHasExited())
                {
                    _log.Info("SteamVR has gone away. Waiting for it to come back.");
                    Disconnect("steamvr_gone", false);
                    return false;
                }
                RefreshControllers();
            }

            // ---- Poses ----
            long before = Stopwatch.GetTimestamp();
            _system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);
            long after = Stopwatch.GetTimestamp();
            sample.Time = Clock.ToSeconds(before + (after - before) / 2);

            sample.Head = ToPose(ref _poses[OpenVR.k_unTrackedDeviceIndex_Hmd]);
            sample.Left = new LinkHand();
            sample.Right = new LinkHand();
            if (_leftIndex < _poses.Length) sample.Left.Pose = ToPose(ref _poses[_leftIndex]);
            if (_rightIndex < _poses.Length) sample.Right.Pose = ToPose(ref _poses[_rightIndex]);
            sample.Left.GripTime = sample.Left.TriggerTime = sample.Time;
            sample.Right.GripTime = sample.Right.TriggerTime = sample.Time;
            sample.Gaze = new LinkGaze();
            sample.Gaze.Direction = new Vec3(1f, 0f, 0f);

            // ---- Grip, trigger, gaze ----
            if (_inputReady)
            {
                EVRInputError update = _input.UpdateActionState(_activeSets, _sizeOfActiveSet);
                double inputNow = Clock.Now;
                if (update != _lastUpdateError)
                {
                    _lastUpdateError = update;
                    if (update != EVRInputError.None) _log.Warn("UpdateActionState says " + update + ". Grip, trigger and gaze are not flowing.");
                    else _log.Info("UpdateActionState is working again.");
                }
                if (update == EVRInputError.None)
                {
                    ReadHand(ref sample.Left, _leftSource, inputNow);
                    ReadHand(ref sample.Right, _rightSource, inputNow);
                    ReadGaze(ref sample);
                }
            }

            FirstSightings(ref sample);
            _detail = "steamvr_streaming";
            return true;
        }

        private void ReadHand(ref LinkHand hand, ulong source, double inputNow)
        {
            InputAnalogActionData_t data = new InputAnalogActionData_t();
            EVRInputError e = _input.GetAnalogActionData(_gripHandle, ref data, _sizeOfAnalog, source);
            bool active = false;
            if (e == EVRInputError.None && data.bActive)
            {
                hand.Grip = data.x;
                hand.GripTime = inputNow + UsableAge(data.fUpdateTime);
                active = true;
                NoteUpdateAge(data.fUpdateTime);
            }
            NoteAnalogError(e);

            data = new InputAnalogActionData_t();
            e = _input.GetAnalogActionData(_triggerHandle, ref data, _sizeOfAnalog, source);
            if (e == EVRInputError.None && data.bActive)
            {
                hand.Trigger = data.x;
                hand.TriggerTime = inputNow + UsableAge(data.fUpdateTime);
                active = true;
            }
            NoteAnalogError(e);

            if (active) hand.Pose.Flags |= LinkPoseFlags.InputActive;
        }

        /// <summary>fUpdateTime should be zero or negative ("this long ago"). Anything else is treated as "just now".</summary>
        private static double UsableAge(float updateTime)
        {
            if (float.IsNaN(updateTime) || float.IsInfinity(updateTime) || updateTime > 0f) return 0.0;
            return updateTime;
        }

        private void ReadGaze(ref LinkSample sample)
        {
            if (_gazeHandle == OpenVR.k_ulInvalidActionHandle || _polls < _gazeRetryAtPoll) return;
            VREyeTrackingData_t eye = new VREyeTrackingData_t();
            EVRInputError e = _input.GetEyeTrackingDataRelativeToNow(_gazeHandle, ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, ref eye, _sizeOfEye);
            if (e != _lastGazeError)
            {
                _lastGazeError = e;
                if (e != EVRInputError.None) _log.Warn("Eye gaze read says " + e + ". Gaze is not flowing; will keep trying every few seconds.");
                else _log.Info("Eye gaze read is answering.");
            }
            if (e != EVRInputError.None)
            {
                _gazeRetryAtPoll = _polls + 1500;   // an error is not worth repeating 500 times a second
                return;
            }
            sample.Gaze = PoseConvert.GazeFromOpenVr(eye.bActive, eye.bValid, eye.bTracked,
                new Vec3(eye.vGazeOrigin.v0, eye.vGazeOrigin.v1, eye.vGazeOrigin.v2),
                new Vec3(eye.vGazeTarget.v0, eye.vGazeTarget.v1, eye.vGazeTarget.v2));
        }

        private static LinkPose ToPose(ref TrackedDevicePose_t p)
        {
            int flags = 0;
            if (p.bDeviceIsConnected) flags |= LinkPoseFlags.Connected;
            if (p.bPoseIsValid) flags |= LinkPoseFlags.PoseValid;
            HmdMatrix34_t m = p.mDeviceToAbsoluteTracking;
            // Row-major 3x4: m0 m1 m2 m3 / m4 m5 m6 m7 / m8 m9 m10 m11, the last column being the position.
            Quat rotation = OpenVrSpace.RotationFromMatrix(m.m0, m.m1, m.m2, m.m4, m.m5, m.m6, m.m8, m.m9, m.m10);
            return PoseConvert.FromOpenVr(
                flags,
                new Vec3(m.m3, m.m7, m.m11),
                rotation,
                new Vec3(p.vVelocity.v0, p.vVelocity.v1, p.vVelocity.v2),
                new Vec3(p.vAngularVelocity.v0, p.vAngularVelocity.v1, p.vAngularVelocity.v2));
        }

        // ------------------------------------------------------------------------------------------
        // Notes for the log, so a person with the headset on can see what worked
        // ------------------------------------------------------------------------------------------

        private void FirstSightings(ref LinkSample s)
        {
            if (!_sawHead && s.Head.IsValid) { _sawHead = true; _log.Info("First valid HEAD pose."); }
            if (!_sawLeft && s.Left.Pose.IsValid) { _sawLeft = true; _log.Info("First valid LEFT hand pose."); }
            if (!_sawRight && s.Right.Pose.IsValid) { _sawRight = true; _log.Info("First valid RIGHT hand pose."); }
            if (!_sawGrip && (s.Left.Grip > 0.5f || s.Right.Grip > 0.5f)) { _sawGrip = true; _log.Info("First GRIP squeeze above half."); }
            if (!_sawTrigger && (s.Left.Trigger > 0.5f || s.Right.Trigger > 0.5f)) { _sawTrigger = true; _log.Info("First TRIGGER pull above half."); }
            if (!_sawGazeActive && (s.Gaze.Flags & LinkGazeFlags.Active) != 0) { _sawGazeActive = true; _log.Info("Eye tracker is ACTIVE."); }
            if (!_sawGazeUsable && s.Gaze.IsUsable) { _sawGazeUsable = true; _log.Info("First usable GAZE reading (active, valid and tracked)."); }

            if (s.Left.Grip != _lastGripLeft) { if (!float.IsNaN(_lastGripLeft)) _gripChanges++; _lastGripLeft = s.Left.Grip; }
            if (s.Right.Grip != _lastGripRight) { if (!float.IsNaN(_lastGripRight)) _gripChanges++; _lastGripRight = s.Right.Grip; }
        }

        private void NoteUpdateAge(float updateTime)
        {
            if (float.IsNaN(updateTime)) return;
            if (updateTime < _updateAgeMin) _updateAgeMin = updateTime;
            if (updateTime > _updateAgeMax) _updateAgeMax = updateTime;
        }

        private void NoteAnalogError(EVRInputError e)
        {
            if (e == _lastAnalogError) return;
            _lastAnalogError = e;
            if (e != EVRInputError.None) _log.Warn("Reading grip / trigger says " + e + ".");
        }

        public string DescribeHealth()
        {
            if (!_connected) return null;
            string ages = _updateAgeMin <= _updateAgeMax
                ? string.Format(CultureInfo.InvariantCulture, "grip fUpdateTime seen from {0:0.0000} to {1:0.0000} s", _updateAgeMin, _updateAgeMax)
                : "no grip readings yet";
            string text = "SteamVR: " + ages + "; grip value changes: " + _gripChanges.ToString(CultureInfo.InvariantCulture)
                + "; head " + (_sawHead ? "ok" : "not seen") + ", left " + (_sawLeft ? "ok" : "not seen") + ", right " + (_sawRight ? "ok" : "not seen")
                + ", gaze " + (_sawGazeUsable ? "ok" : (_sawGazeActive ? "active but never usable" : "not seen")) + ".";
            _updateAgeMin = double.MaxValue;
            _updateAgeMax = double.MinValue;
            _gripChanges = 0;
            return text;
        }

        private void Problem(string message)
        {
            // Say each distinct problem once, not every two seconds.
            if (message == _lastProblem) return;
            _lastProblem = message;
            _log.Info(message);
        }

        private static string Safe(Func<string> read)
        {
            try { return read(); } catch (Exception) { return "?"; }
        }

        public void Dispose()
        {
            Disconnect("stopped", false);
        }
    }
}
