using Xunit;

namespace Portaballs.Core.Tests
{
    /// <summary>
    /// Default (Portal-style, paint-centre) player crossing and the arrival check against the exit floor.
    /// Entry: orange on the far wall, patch centre 1.5 m above a floor at z = 0. Exit: blue on the near wall.
    /// </summary>
    public class PlayerArrivalTests
    {
        private static readonly Vec3 FeetGoingIn = new Vec3(5.1f, 0f, 0f);

        private static PortalPair DefaultPair(float exitCentreHeight, float exitFloor = 0f)
        {
            var settings = new PortalSettings();
            PortalEnd a = PortalEnd.Create(Rig.FarWall(), Rig.Opening(PaintColor.Orange, new Vec2(2f, 1.5f)), 0f, settings);
            PortalEnd b = PortalEnd.Create(Rig.NearWall(exitFloor),
                Rig.Opening(PaintColor.Blue, new Vec2(1f, exitCentreHeight - exitFloor)), exitFloor, settings);
            return new PortalPair(a, b);
        }

        [Fact]
        public void DefaultAnchorIsPaintCentreForEverything()
        {
            Assert.Equal(PortalAnchor.PaintCenter, new PortalSettings().Anchor);
            PortalPair pair = DefaultPair(2f);
            Assert.Equal(PortalAnchor.PaintCenter, pair.A.AnchorUsed);
            Assert.Equal(PortalAnchor.PaintCenter, pair.B.AnchorUsed);
            // Player version maps patch centre to patch centre, rigidly.
            Rig.Near(new Vec3(0f, -1f, 2f), pair.PlayerAToB.TransformPoint(new Vec3(5f, 0f, 1.5f)));
        }

        [Fact]
        public void ExitHigherThanEntryDropsThePlayer()
        {
            // Exit patch centre 2.0 m up, entry 1.5 m: feet arrive 0.5 m above the floor and fall.
            PortalPair pair = DefaultPair(2f);
            Vec3 feet = pair.PlayerAToB.TransformPoint(FeetGoingIn);
            Rig.Near(new Vec3(0.1f, -1f, 0.5f), feet);
            PlayerArrival arrival = PlayerArrival.Check(feet, 0f);
            Assert.Equal(0.5f, arrival.HeightAboveFloor, 4);
            Assert.Equal(0.5f, arrival.FallDistance, 4);
            Assert.Equal(0f, arrival.LiftNeeded);
            Rig.Near(feet, arrival.Feet);
        }

        [Fact]
        public void ExitLowerThanEntryLiftsThePlayer()
        {
            // Exit patch centre 1.0 m up: feet would be 0.5 m inside the floor, so lift by 0.5 m.
            PortalPair pair = DefaultPair(1f);
            Vec3 feet = pair.PlayerAToB.TransformPoint(FeetGoingIn);
            Rig.Near(new Vec3(0.1f, -1f, -0.5f), feet);
            PlayerArrival arrival = PlayerArrival.Check(feet, 0f);
            Assert.Equal(-0.5f, arrival.HeightAboveFloor, 4);
            Assert.Equal(0f, arrival.FallDistance);
            Assert.Equal(0.5f, arrival.LiftNeeded, 4);
            Rig.Near(new Vec3(0.1f, -1f, 0f), arrival.Feet);
        }

        [Fact]
        public void EqualHeightsArriveOnTheFloor()
        {
            PortalPair pair = DefaultPair(1.5f);
            PlayerArrival arrival = PlayerArrival.Check(pair.PlayerAToB.TransformPoint(FeetGoingIn), 0f);
            Assert.Equal(0f, arrival.HeightAboveFloor, 4);
            Assert.Equal(0f, arrival.FallDistance, 4);
            Assert.Equal(0f, arrival.LiftNeeded, 4);
        }

        [Fact]
        public void RaisedExitRoomWithLowPatchNeedsALift()
        {
            // Exit room floor at z = 2, patch centre 0.8 m up its wall (z = 2.8). Feet land at 1.3: 0.7 m under.
            PortalPair pair = DefaultPair(2.8f, 2f);
            PlayerArrival arrival = PlayerArrival.Check(pair.PlayerAToB.TransformPoint(FeetGoingIn), 2f);
            Assert.Equal(-0.7f, arrival.HeightAboveFloor, 4);
            Assert.Equal(0.7f, arrival.LiftNeeded, 4);
            Rig.Near(new Vec3(0.1f, -1f, 2f), arrival.Feet);
        }
    }
}
