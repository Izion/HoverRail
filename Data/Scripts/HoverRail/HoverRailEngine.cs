using System;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.Common;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Common.Components;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace HoverRail {
	[MyEntityComponentDescriptor(typeof(MyObjectBuilder_TerminalBlock), "HoverRail_Engine_Large")]
	public class HoverRailEngine : MyGameLogicComponent {
		Sandbox.Common.ObjectBuilders.MyObjectBuilder_EntityBase objectBuilder = null;
		SlidingAverageVector avgGuidance, avgCorrectF, avgDampenF;
		bool block_initialized = false;
        public override void Init(Sandbox.Common.ObjectBuilders.MyObjectBuilder_EntityBase objectBuilder) {
			this.avgGuidance = new SlidingAverageVector(0.2);
			this.avgCorrectF = new SlidingAverageVector(0.9);
			this.avgDampenF = new SlidingAverageVector(0.9);
			Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
			this.objectBuilder = objectBuilder;
			this.id = HoverRailEngine.attachcount++;
			// MyLog.Default.WriteLine(String.Format("ATTACH TO OBJECT {0}", this.id));
		}
		
		float power_usage = 0.0f;
		MyResourceSinkComponent sinkComp;
		float GetCurrentPowerDraw() {
			// MyLog.Default.WriteLine(String.Format("report power usage as {0}", power_usage));
			return power_usage;
		}
		public void UpdatePowerUsage(float force) {
			power_usage = force;
			// sinkComp.Update();
		}
		
		public void InitLate() {
			block_initialized = true;
			
			// init power usage
			// disabled! because it does not work.
			/* 
			Entity.Components.TryGet<MyResourceSinkComponent>(out sinkComp);
			if (sinkComp == null) {
				MyLog.Default.WriteLine("set up new power sink");
				sinkComp = new MyResourceSinkComponent();
				sinkComp.Init(
					MyStringHash.GetOrCompute("Maglev"),
					45000000,
					GetCurrentPowerDraw
				);
				Entity.Components.Add(sinkComp);
			} else {
				MyLog.Default.WriteLine("reuse existing power sink");
				sinkComp.SetRequiredInputFuncByType(MyResourceDistributorComponent.ElectricityId, GetCurrentPowerDraw);
			}
			*/
			
			if (!EngineUI.initialized) EngineUI.InitLate();
		}
		static int attachcount;
		int id;
		private int frame = 0;
		
		RailGuide activeRailGuide;
		public void LookForNewRail(Vector3D searchCenter, Vector3D hoverCenter) {
			var area = new BoundingSphereD(searchCenter, 2.5);
			var items = MyAPIGateway.Entities.GetEntitiesInSphere(ref area);
			foreach (var ent in items) {
				var railGuide = RailGuide.fromEntity(ent);
				if (railGuide != null) {
					var guidance = new Vector3D(0,0,0);
					var test = railGuide.getGuidance(hoverCenter, ref guidance);
					if (test) {
						activeRailGuide = railGuide;
						return;
					}
				}
			}
			activeRailGuide = null;
		}
		
		public override void UpdateBeforeSimulation() {
			if (!block_initialized) InitLate();
			
			frame++;
			
			if (!(bool) SettingsStore.Get(Entity, "power_on", true)) return;
			
			double forceLimit = (double) (float) SettingsStore.Get(Entity, "force_slider", 100000.0f);
			
			var hoverCenter = Entity.WorldMatrix.Translation;
			var searchCenter = Entity.WorldMatrix.Translation + Entity.WorldMatrix.Down * 2.499;
			DebugDraw.Sphere(searchCenter, 2.5f, Color.Green);
			
			var guidance = new Vector3D(0, 0, 0);
			if (activeRailGuide == null || !activeRailGuide.getGuidance(hoverCenter, ref guidance)) {
				this.LookForNewRail(searchCenter, hoverCenter);
				if (activeRailGuide == null) return;
				activeRailGuide.getGuidance(hoverCenter, ref guidance); // guaranteed to succeed (once)
			}
			
			var activeBlock = activeRailGuide.cubeBlock;
			DebugDraw.Sphere(activeBlock.WorldMatrix.Translation, 0.1f, Color.Red);
			DebugDraw.Sphere(activeBlock.WorldMatrix.Translation + activeBlock.WorldMatrix.Up * (0.5 + 0.5 * Math.Sin(frame / 10.0)), 0.1f, Color.Red);
			
			DebugDraw.Sphere(searchCenter, 0.1f, Color.Green);
			
			float force_magnitude = 0;
			// correction force, pushes engine towards rail guide
			{
				var len = guidance.Length() / 2.5; // 0 .. 1
				var weight = len;
				if (weight > 0.99) weight = 0.99; // always some force
				const double splitPoint = 0.5;
				if (weight > splitPoint) weight = 1.0 - (weight - splitPoint) / (1.0 - splitPoint);
				else weight = weight / splitPoint;
				var factor = Math.Pow(weight, 2.0); // spiken
				var guidanceForce = forceLimit * Vector3D.Normalize(guidance) * factor;
				this.avgCorrectF.update(guidanceForce);
				DebugDraw.Sphere(searchCenter + this.avgCorrectF.value * 0.000001f, 0.1f, Color.Yellow);
				activeRailGuide.applyForces(Entity, this.avgCorrectF.value);
				force_magnitude += (float) this.avgCorrectF.value.Length();
			}
			// dampening force, reduces oscillation over time
			var dF = guidance - this.avgGuidance.value;
			{
				// var len = guidance.Length() / 2.5;
				// if (len > 0.99) len = 0.99;
				// var factor = Math.Pow(len, 0.3);
				var factor = 1.0;
				var dampenForce = forceLimit * 0.5 * dF * factor; // separate slider?
				this.avgDampenF.update(dampenForce);
				DebugDraw.Sphere(searchCenter + this.avgDampenF.value * 0.000001f, 0.1f, Color.Red);
				activeRailGuide.applyForces(Entity, this.avgDampenF.value);
				force_magnitude += (float) this.avgCorrectF.value.Length();
			}
			this.avgGuidance.update(guidance);
			UpdatePowerUsage(force_magnitude);
		}
		public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false) {
			return objectBuilder;
		}
	}

	static class EngineUI {
		public static bool initialized = false;
		public static IMyTerminalControlSlider forceSlider;
		public static IMyTerminalControlOnOffSwitch powerSwitch;
		
		public static bool BlockIsEngine(IMyTerminalBlock block) {
			return block.BlockDefinition.SubtypeId == "HoverRail_Engine_Large";
		}
		public static float LogRound(float f) {
			var logbase = Math.Pow(10, Math.Floor(Math.Log10(f)));
			var frac = f / logbase;
			frac = Math.Floor(frac);
			return (float) (logbase * frac);
		}
		public static string SIFormat(float f) {
			if (f > 1000000000) return String.Format("{0}G", Math.Round(f / 1000000000, 2));
			if (f > 1000000) return String.Format("{0}M", Math.Round(f / 1000000, 2));
			if (f > 1000) return String.Format("{0}K", Math.Round(f / 1000, 2));
			if (f > 1) return String.Format("{0}", Math.Round(f, 2));
			if (f > 0.0001) return String.Format("{0}m", Math.Round(f * 1000, 2));
			if (f > 0.0000001) return String.Format("{0}n", Math.Round(f * 1000000, 2));
			// give up
			return String.Format("{0}", f);
		}
		public static void InitLate() {
			initialized = true;
			
            powerSwitch = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyTerminalBlock>("HoverRail_OnOff");
            powerSwitch.Title   = MyStringId.GetOrCompute("Maglev Engine");
			powerSwitch.Tooltip = MyStringId.GetOrCompute("Enable to apply force to stick to the track.");
            powerSwitch.Getter  = b => (bool) SettingsStore.Get(b, "power_on", true);
            powerSwitch.Setter  = (b, v) => SettingsStore.Set(b, "power_on", v);
			powerSwitch.OnText  = MyStringId.GetOrCompute("On");
			powerSwitch.OffText = MyStringId.GetOrCompute("Off");
			powerSwitch.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyTerminalBlock>(powerSwitch);
			
			forceSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyTerminalBlock>( "HoverRail_ForceLimit" );
			forceSlider.Title   = MyStringId.GetOrCompute("Force Limit");
			forceSlider.Tooltip = MyStringId.GetOrCompute("The amount of force applied to align this motor with the track.");
			forceSlider.SetLogLimits(10000.0f, 50000000.0f);
			forceSlider.SupportsMultipleBlocks = true;
			forceSlider.Getter  = b => (float) SettingsStore.Get(b, "force_slider", 100000.0f);
			forceSlider.Setter  = (b, v) => SettingsStore.Set(b, "force_slider", (float) LogRound(v));
			forceSlider.Writer  = (b, result) => result.Append(String.Format("{0}N", SIFormat((float) SettingsStore.Get(b, "force_slider", 100000.0f))));
			forceSlider.Visible = BlockIsEngine;
			MyAPIGateway.TerminalControls.AddControl<IMyTerminalBlock>(forceSlider);
		}
	}
}
