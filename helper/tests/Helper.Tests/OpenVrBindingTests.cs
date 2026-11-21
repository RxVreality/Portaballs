using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Portaballs.Core;
using Valve.VR;
using Xunit;

namespace Portaballs.Helper.Tests
{
    /// <summary>
    /// What can be checked about the SteamVR side with SteamVR switched off. None of these start
    /// SteamVR or talk to it.
    /// </summary>
    public class OpenVrBindingTests
    {
        [Fact]
        public void The_binding_is_the_version_the_helper_was_written_against()
        {
            Assert.Equal("IVRInput_011", OpenVR.IVRInput_Version);
            Assert.Equal("IVRSystem_026", OpenVR.IVRSystem_Version);
            Assert.Equal(64u, OpenVR.k_unMaxTrackedDeviceCount);
            Assert.Equal(2, (int)EVRApplicationType.VRApplication_Overlay);
            Assert.Equal(3, (int)EVRApplicationType.VRApplication_Background);
            Assert.Equal(1, (int)ETrackingUniverseOrigin.TrackingUniverseStanding);
            Assert.Equal(700, (int)EVREventType.VREvent_Quit);
        }

        [Fact]
        public void Structures_handed_to_SteamVR_have_the_sizes_the_C_header_gives_them_on_64_bit_Windows()
        {
            // SteamVR checks these sizes and refuses the call when they are wrong.
            Assert.Equal(8, IntPtr.Size);
            Assert.Equal(80, Marshal.SizeOf(typeof(TrackedDevicePose_t)));        // 3x4 floats + 2 vectors + enum + 2 bools, padded
            Assert.Equal(48, Marshal.SizeOf(typeof(InputAnalogActionData_t)));    // bool, 64-bit handle, 7 floats, padded to 8
            Assert.Equal(24, Marshal.SizeOf(typeof(InputDigitalActionData_t)));
            Assert.Equal(96, Marshal.SizeOf(typeof(InputPoseActionData_t)));
            Assert.Equal(28, Marshal.SizeOf(typeof(VREyeTrackingData_t)));        // 3 bools (padded to 4) + 2 vectors
            Assert.Equal(32, Marshal.SizeOf(typeof(VRActiveActionSet_t)));
        }

        [Fact]
        public void The_OpenVR_library_sits_beside_the_program_with_its_licence_and_action_files()
        {
            string folder = AppContext.BaseDirectory;
            Assert.True(File.Exists(Path.Combine(folder, "openvr_api.dll")), "openvr_api.dll missing from " + folder);
            Assert.True(File.Exists(Path.Combine(folder, "openvr_LICENSE.txt")));
            Assert.Contains("Valve Corporation", File.ReadAllText(Path.Combine(folder, "openvr_LICENSE.txt")));
            Assert.True(File.Exists(Path.Combine(folder, "actions", "actions.json")));
            Assert.True(File.Exists(Path.Combine(folder, "actions", "bindings_frame_controller.json")));
        }

        [Fact]
        public void The_OpenVR_library_loads_and_answers_a_question_that_needs_no_SteamVR()
        {
            // Only asks whether a runtime is registered on disk. A wrong or missing DLL throws here.
            bool installed = OpenVR.IsRuntimeInstalled();
            Assert.True(installed || !installed);
        }

        [Fact]
        public void The_action_files_name_exactly_the_actions_the_helper_asks_for()
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "actions");
            using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "actions.json"))))
            {
                string all = manifest.RootElement.GetProperty("actions").GetRawText();
                Assert.Contains("\"/actions/link/in/grip\"", all);
                Assert.Contains("\"/actions/link/in/trigger\"", all);
                Assert.Contains("\"/actions/link/in/gaze\"", all);
                Assert.Contains("\"eyetracking\"", all);
                Assert.Equal("/actions/link", manifest.RootElement.GetProperty("action_sets")[0].GetProperty("name").GetString());

                foreach (JsonElement binding in manifest.RootElement.GetProperty("default_bindings").EnumerateArray())
                {
                    string file = Path.Combine(folder, binding.GetProperty("binding_url").GetString());
                    Assert.True(File.Exists(file), "missing " + file);
                    using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file)))
                    {
                        Assert.Equal(binding.GetProperty("controller_type").GetString(), doc.RootElement.GetProperty("controller_type").GetString());
                        JsonElement set;
                        Assert.True(doc.RootElement.GetProperty("bindings").TryGetProperty("/actions/link", out set));
                    }
                }
            }
        }

        [Fact]
        public void With_SteamVR_off_the_source_waits_and_never_loads_SteamVR()
        {
            if (Process.GetProcessesByName("vrserver").Length > 0) return;   // someone is in VR: leave their session alone

            using (FileLog log = new FileLog(null, false))
            using (OpenVrSource source = new OpenVrSource(log, "overlay", AppContext.BaseDirectory))
            {
                LinkSample sample = new LinkSample();
                for (int i = 0; i < 3; i++) Assert.False(source.TryRead(ref sample));
                Assert.Equal("openvr", source.Name);
                Assert.Equal("waiting_for_steamvr", source.Detail);
                Assert.Null(source.DescribeHealth());
            }
            Assert.Empty(Process.GetProcessesByName("vrserver"));
            Assert.Empty(Process.GetProcessesByName("vrmonitor"));
        }
    }
}
