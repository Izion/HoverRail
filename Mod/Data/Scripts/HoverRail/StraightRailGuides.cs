using System;
using VRage.Game.ModAPI;
using VRageMath;

static class StraightRailConstants {
	// engine still catches this far off the block. important as it allows us to transition betweeen rails safely
	// bit less than half a block, so we can bridge a 2/3-block gap safely
	public const float OVERHANG = 1.2f;
}

namespace HoverRail {
	abstract class StraightRailGuide : RailGuide {
		float halfsize;
		public StraightRailGuide(IMyCubeBlock cubeBlock, float halfsize) : base(cubeBlock) { this.halfsize = halfsize; }
		// also used in sloped rail
		public static bool straight_guidance(float halfsize, bool horizontalForce, MatrixD cubeMatrix, Vector3D localCoords,
			ref Vector3D guide, ref float weight, float height,
			bool apply_overhang = true
		) {
			localCoords.Y -= height - 1.25;
			var overhang = (apply_overhang?StraightRailConstants.OVERHANG:0);
			if (localCoords.X < -halfsize - overhang || localCoords.X > halfsize + overhang) return false; // outside the box
			if (localCoords.Y < -1.25 || localCoords.Y > 2.60) return false; // some leeway above TODO lower
			if (localCoords.Z < -1.25 || localCoords.Z > 1.25) return false; // outside the box
			
			float myWeight;
			const float underhang = 1.5f; // start transition early
			float hang = overhang + underhang;
			float transitionStart = halfsize - underhang;
			if (localCoords.X < -transitionStart || localCoords.X > transitionStart) {
				// influence goes down to 0 
				myWeight = (float) ((hang - (float) (Math.Abs(localCoords.X) - transitionStart)) / hang);
			} else {
				myWeight = 1.0f;
			}
			
			var localRail = new Vector3D(localCoords.X, height - 1.25, horizontalForce ? 0 : localCoords.Z);
			// DebugDraw.Sphere(Vector3D.Transform(localRail, cubeMatrix), 0.2f, Color.Red);
			weight += myWeight;
			guide += Vector3D.Transform(localRail, cubeMatrix) * myWeight;
			return true;
		}
		public override bool GetGuidance(Vector3D pos, bool horizontalForce, ref Vector3D guide, ref float weight, float height) {
			if (!base.GetGuidance(pos, horizontalForce, ref guide, ref weight, height)) return false;
			
			var localCoords = Vector3D.Transform(pos, this.cubeBlock.WorldMatrixNormalizedInv);
			return straight_guidance(halfsize, horizontalForce, this.cubeBlock.WorldMatrix, localCoords, ref guide, ref weight, height);
		}
	}
	class Straight1xRailGuide : StraightRailGuide {
		public Straight1xRailGuide(IMyCubeBlock cubeBlock) : base(cubeBlock, 1.25f) { }
	}
	
	class Straight3xRailGuide : StraightRailGuide {
		public Straight3xRailGuide(IMyCubeBlock cubeBlock) : base(cubeBlock, 3.75f) { }
	}
	
	class Straight10xRailGuide : StraightRailGuide {
		public Straight10xRailGuide(IMyCubeBlock cubeBlock) : base(cubeBlock, 12.5f) { }
	}
	
	class Straight30xRailGuide : StraightRailGuide {
		public Straight30xRailGuide(IMyCubeBlock cubeBlock) : base(cubeBlock, 37.5f) { }
	}
}
