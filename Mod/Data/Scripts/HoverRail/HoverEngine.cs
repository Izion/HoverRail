using System;
using System.Text;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.Common.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using VRage.Game.Components;
using VRage.ObjectBuilders;
using VRage.ModAPI;

namespace HoverRail
{
    public class HoverEngine : MyGameLogicComponent
    {
        const float MAX_POWER_USAGE_MW = 1f;
        const float FORCE_POWER_COST_MW_N = 0.0000001f;
        SlidingAverageVector avgGuidance, avgCorrectF, avgDampenF;
        MyResourceSinkComponent sinkComp;
        bool block_initialized = false;
        MyEntity3DSoundEmitter engine_sound;
        MySoundPair sound_engine_start, sound_engine_loop;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            this.avgGuidance = new SlidingAverageVector(0.3);
            this.avgCorrectF = new SlidingAverageVector(0.9);
            this.avgDampenF = new SlidingAverageVector(0.9);
            this.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
            this.id = attachcount++;
            this.activeRailGuides = new HashSet<RailGuide>();
            this.sound_engine_start = new MySoundPair("HoverEngine_Startup");
            this.sound_engine_loop = new MySoundPair("HoverEngine_Loop");
            MyEntity3DSoundEmitter.PreloadSound(sound_engine_start);
            MyEntity3DSoundEmitter.PreloadSound(sound_engine_loop);
            this.engine_sound = new MyEntity3DSoundEmitter(Entity as VRage.Game.Entity.MyEntity);
            this.engine_sound.Force3D = true;
            // MyLog.Default.WriteLine(String.Format("ATTACH TO OBJECT {0}", this.id));
            InitPowerComp();
        }

        void EngineCustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            builder.AppendLine(String.Format("Required Power: {0}W", EngineUIMini.SIFormat(power_usage * 1000000)));
            builder.AppendLine(String.Format("Max Required Power: {0}W", EngineUIMini.SIFormat(MAX_POWER_USAGE_MW * 1000000)));
            builder.AppendLine(String.Format("Current Height: {0}M", (float)SettingsStore.Get(Entity, "height_offset", EngineUIMini.DefaultHeightM)));
            builder.AppendLine(String.Format("Target Height: {0}M", (float)SettingsStore.Get(Entity, "height_target", EngineUIMini.DefaultHeightM)));
        }

        // init power usage
        void InitPowerComp()
        {
            Entity.Components.TryGet<MyResourceSinkComponent>(out sinkComp);
            if (sinkComp == null)
            {
                // MyLog.Default.WriteLine("set up new power sink");
                sinkComp = new MyResourceSinkComponent();
                sinkComp.Init(
                    MyStringHash.GetOrCompute("Thrust"),
                    MAX_POWER_USAGE_MW,
                    GetCurrentPowerDraw,
                    (MyCubeBlock)Entity
                );
                Entity.Components.Add(sinkComp);
            }
            else
            {
                // MyLog.Default.WriteLine("reuse existing power sink");
                sinkComp.SetRequiredInputFuncByType(MyResourceDistributorComponent.ElectricityId, GetCurrentPowerDraw);
            }
            (Entity as IMyTerminalBlock).AppendingCustomInfo += EngineCustomInfo;
        }

        float power_usage = 0.0f;
        float power_ratio_available = 1.0f;

        float GetCurrentPowerDraw()
        {
            // MyLog.Default.WriteLine(String.Format("report power usage as {0}", power_usage));
            return power_usage;
        }

        public void UpdatePowerUsage(float new_power)
        {
            if (power_usage == new_power) return;
            power_ratio_available = 1.0f;
            engine_sound.CustomVolume = (float)(1.0 + new_power * 10); // 100KW = 100% volume
            if (new_power > MAX_POWER_USAGE_MW)
            {
                power_ratio_available = MAX_POWER_USAGE_MW / new_power;
                new_power = MAX_POWER_USAGE_MW;
            }
            // MyLog.Default.WriteLine(String.Format("set power to {0}", new_power));
            power_usage = new_power;
            sinkComp.Update();
        }

        public void InitLate()
        {
            block_initialized = true;
            if (!EngineUIMini.initialized) EngineUIMini.InitLate(Entity as IMyTerminalBlock);
        }
        static int attachcount;
        int id;
        private int frame = 0;
        private bool last_power_state = false;

        HashSet<RailGuide> activeRailGuides;

        void QueueLoopSound(MyEntity3DSoundEmitter emitter)
        {
            emitter.StoppedPlaying -= QueueLoopSound;
            emitter.PlaySingleSound(sound_engine_loop, true);
        }

        public void UpdatePowerState(bool state_on)
        {
            bool state_changed = last_power_state != state_on;
            last_power_state = state_on;

            // People kept reporting that the engine hum was audible at any distance.
            // I have no fucking clue why that would be the case, but it was probably super annoying, so the hum is just off now.
            // ---
            // Added this as a terminal option - testing

            if ((bool)SettingsStore.Get(Entity, "sound_toggle", false))
            {
                if (state_on == false)
                {
                    if (state_changed)
                    {
                        engine_sound.StoppedPlaying -= QueueLoopSound;
                        engine_sound.StopSound(true);
                    }
                }
                else
                {
                    if (state_changed || !engine_sound.IsPlaying)
                    {
                        engine_sound.StoppedPlaying -= QueueLoopSound;
                        engine_sound.StopSound(true); // ... why??
                        engine_sound.PlaySingleSound(sound_engine_start, true);
                        engine_sound.StoppedPlaying += QueueLoopSound;
                    }
                }
            }
        }

        public override void UpdateBeforeSimulation()
        {
            base.UpdateBeforeSimulation();

            if (!block_initialized) InitLate();

            frame++;

            if (frame % 10 == 0) (Entity as IMyTerminalBlock).RefreshCustomInfo();

            if (!(Entity as IMyFunctionalBlock).Enabled)
            {
                UpdatePowerUsage(0);
                UpdatePowerState(false);
                return;
            }

            // this will be one frame late ... but close enough??
            // power requested that can be satisfied by the network * power required that can be requested given our max
            float power_ratio = sinkComp.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) * power_ratio_available;

            if (!sinkComp.IsPoweredByType(MyResourceDistributorComponent.ElectricityId))
            {
                power_ratio = 0;
            }
            // MyLog.Default.WriteLine(String.Format("power ratio is {0}", power_ratio));

            ChangeEngineHeight();

            float height = (float)SettingsStore.Get(Entity, "height_offset", EngineUIMini.DefaultHeightM);

            double forceLimit = (double)(float)SettingsStore.Get(Entity, "force_slider", EngineUIMini.DefaultForceMWN);
            bool horizontalForce = (bool)SettingsStore.Get(Entity, "horizontal_force", true);

            var hoverCenter = Entity.WorldMatrix.Translation;
            var searchCenter = Entity.WorldMatrix.Translation + Entity.WorldMatrix.Down * 2.499;
            // DebugDraw.Sphere(searchCenter, 2.5f, Color.Green);

            var rail_pos = new Vector3D(0, 0, 0);
            var weight_sum = 0.0f;
            HashSet<RailGuide> lostGuides = new HashSet<RailGuide>();
            RailGuide anyRailGuide = null;

            foreach (var guide in activeRailGuides)
            {
                if (!guide.getGuidance(hoverCenter, horizontalForce, ref rail_pos, ref weight_sum, height))
                {
                    // lost rail lock
                    lostGuides.Add(guide);
                    continue;
                }
                anyRailGuide = guide;
            }

            foreach (var guide in lostGuides)
            {
                activeRailGuides.Remove(guide);
            }
            lostGuides.Clear();

            if (weight_sum < 0.9f)
            {
                // not confident in our rail lock, look for possible new rails
                var area = new BoundingSphereD(searchCenter, 2.5);
                var items = MyAPIGateway.Entities.GetEntitiesInSphere(ref area);
                rail_pos = Vector3D.Zero;
                weight_sum = 0.0f;
                foreach (var ent in items)
                {
                    var guide = RailGuide.fromEntity(ent);

                    if (guide != null)
                    {
                        var test = guide.getGuidance(hoverCenter, horizontalForce, ref rail_pos, ref weight_sum, height);

                        if (test)
                        {
                            activeRailGuides.Add(guide);
                            anyRailGuide = guide;
                        }
                    }
                }
            }

            // MyLog.Default.WriteLine(String.Format("{0}:- hovering at {1}", Entity.EntityId, hoverCenter));
            if (activeRailGuides.Count == 0)
            {
                UpdatePowerUsage(0);
                UpdatePowerState(true); // powered but idle
                return;
            }

            // average by weight
            rail_pos /= weight_sum;

            var guidance = rail_pos - hoverCenter;
            // MyLog.Default.WriteLine(String.Format("{0}: rail pos is {1}, due to weight correction by {2}; guidance {3}", Entity.EntityId, rail_pos, weight_sum, guidance));
            DebugDraw.Sphere(rail_pos, 0.15f, Color.Blue);
            DebugDraw.Sphere(rail_pos * 0.5 + hoverCenter * 0.5, 0.1f, Color.Blue);
            DebugDraw.Sphere(hoverCenter, 0.1f, Color.Blue);

            // DebugDraw.Sphere(searchCenter, 0.1f, Color.Green);

            float force_magnitude = 0;
            // correction force, pushes engine towards rail guide
            {
                var len = guidance.Length() / 2.5; // 0 .. 1

                if (len > 0.001)
                {
                    var weight = len;

                    if (weight > 0.99) weight = 0.99; // always some force

                    const double splitPoint = 0.5;

                    if (weight > splitPoint) weight = 1.0 - (weight - splitPoint) / (1.0 - splitPoint);
                    else weight = weight / splitPoint;

                    var factor = Math.Pow(weight, 2.0); // spiken
                    var guidanceForce = forceLimit * Vector3D.Normalize(guidance) * factor;
                    this.avgCorrectF.update(guidanceForce);
                    DebugDraw.Sphere(searchCenter, 0.1f, Color.Yellow);
                    anyRailGuide.applyForces(Entity, this.avgCorrectF.value * power_ratio);
                    force_magnitude += (float)this.avgCorrectF.value.Length();
                }
            }
            // dampening force, reduces oscillation over time
            var dF = guidance - this.avgGuidance.value;

            {
                // var len = guidance.Length() / 2.5;
                // if (len > 0.99) len = 0.99;
                // var factor = Math.Pow(len, 0.3);
                var factor = 1.0;
                var dampenForce = forceLimit * (float)SettingsStore.Get(Entity, "dampen_force", 0.5f) * dF * factor; // separate slider? - okay :)
                this.avgDampenF.update(dampenForce);
                DebugDraw.Sphere(searchCenter + this.avgDampenF.value * 0.000001f, 0.1f, Color.Red);
                anyRailGuide.applyForces(Entity, this.avgDampenF.value * power_ratio);
                force_magnitude += (float)this.avgDampenF.value.Length();
            }
            this.avgGuidance.update(guidance);
            UpdatePowerUsage(force_magnitude * FORCE_POWER_COST_MW_N);
            UpdatePowerState(true);
        }

        private void ChangeEngineHeight()
        {
            float height = (float)SettingsStore.Get(Entity, "height_offset", EngineUIMini.DefaultHeightM);
            float target = (float)SettingsStore.Get(Entity, "height_target", EngineUIMini.DefaultHeightM);

            if (target != height)
            {
                if (target > height)
                {
                    height = (height + 0.01f);
                }
                else if (target < height)
                {
                    height = (height - 0.01f);
                }
            }

            SettingsStore.Set(Entity, "height_offset", (float)Math.Round(height, 3));
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_UpgradeModule), false, "HoverRail_SmallHoverEngineSmall")]
    public class HoverEngineSmall3x1 : HoverEngine
    {
    }

    static class EngineUIMini
    {
        public static bool initialized = false;
        public static IMyTerminalControlSlider forceSlider, heightSlider, damperSlider;
        public static IMyTerminalAction lowerHeightAction, raiseHeightAction;
        public static IMyTerminalControlOnOffSwitch soundOnOffSwitch, horizontalForceSwitch;

        public static float DefaultHeightM = 0.9f;
        public static float DefaultForceMWN = 10000.0f;

        public static bool BlockIsEngine(IMyTerminalBlock block)
        {
            return block.BlockDefinition.SubtypeId == "HoverRail_SmallHoverEngineSmall";
        }

        public static float LogRound(float f)
        {
            var logbase = Math.Pow(10, Math.Floor(Math.Log10(f)));
            var frac = f / logbase;
            frac = Math.Floor(frac);
            return (float)(logbase * frac);
        }

        public static string SIFormat(float f)
        {
            if (f >= 1000000000) return String.Format("{0}G", Math.Round(f / 1000000000, 2));
            if (f >= 1000000) return String.Format("{0}M", Math.Round(f / 1000000, 2));
            if (f >= 1000) return String.Format("{0}k", Math.Round(f / 1000, 2));
            if (f >= 1) return String.Format("{0}", Math.Round(f, 2));
            if (f >= 0.0001) return String.Format("{0}m", Math.Round(f * 1000, 2));
            if (f >= 0.0000001) return String.Format("{0}n", Math.Round(f * 1000000, 2));
            // give up
            return String.Format("{0}", f);
        }

        public static void GetEngineActions(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            if (!BlockIsEngine(block))
            {
                actions.Remove(lowerHeightAction);
                actions.Remove(raiseHeightAction);
            }
        }

        public static void LowerHeightAction(IMyTerminalBlock block)
        {
            float height = (float)SettingsStore.Get(block, "height_offset", DefaultHeightM);
            height = Math.Max(0.1f, (float)Math.Round(height - 0.1f, 1));
            SettingsStore.Set(block, "height_offset", height);
        }

        public static void RaiseHeightAction(IMyTerminalBlock block)
        {
            float height = (float)SettingsStore.Get(block, "height_offset", DefaultHeightM);
            height = Math.Min(2.5f, (float)Math.Round(height + 0.1f, 1));
            SettingsStore.Set(block, "height_offset", height);
        }

        public static void InitLate(IMyTerminalBlock block)
        {
            // MyLog.Default.WriteLine("EngineUI init!");
            initialized = true;

            // Optional sounds toggle
            soundOnOffSwitch = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyUpgradeModule>("Sound_TurnOnOff");
            soundOnOffSwitch.Title = MyStringId.GetOrCompute("Engine Sound");
            soundOnOffSwitch.Tooltip = MyStringId.GetOrCompute("Toggles the sound of the engines");
            soundOnOffSwitch.Getter = b => (bool)SettingsStore.Get(b, "sound_toggle", false);
            soundOnOffSwitch.Setter = (b, v) => SettingsStore.Set(b, "sound_toggle", v);
            soundOnOffSwitch.OnText = MyStringId.GetOrCompute("On");
            soundOnOffSwitch.OffText = MyStringId.GetOrCompute("Off");
            soundOnOffSwitch.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(soundOnOffSwitch);

            horizontalForceSwitch = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyUpgradeModule>("HoverRail_HorizontalForce");
            horizontalForceSwitch.Title = MyStringId.GetOrCompute("Horizontal Force");
            horizontalForceSwitch.Tooltip = MyStringId.GetOrCompute("Whether the engine exerts horizontal force.");
            horizontalForceSwitch.Getter = b => (bool)SettingsStore.Get(b, "horizontal_force", true);
            horizontalForceSwitch.Setter = (b, v) => SettingsStore.Set(b, "horizontal_force", v);
            horizontalForceSwitch.OnText = MyStringId.GetOrCompute("On");
            horizontalForceSwitch.OffText = MyStringId.GetOrCompute("Off");
            horizontalForceSwitch.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(horizontalForceSwitch);

            forceSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("HoverRail_ForceLimit");
            forceSlider.Title = MyStringId.GetOrCompute("Force Limit");
            forceSlider.Tooltip = MyStringId.GetOrCompute("The amount of force applied to align this motor with the track.");
            forceSlider.SetLogLimits(10000.0f, 30000000.0f);
            forceSlider.SupportsMultipleBlocks = true;
            forceSlider.Getter = b => (float)SettingsStore.Get(b, "force_slider", DefaultForceMWN);
            forceSlider.Setter = (b, v) => SettingsStore.Set(b, "force_slider", (float)LogRound(v));
            forceSlider.Writer = (b, result) => result.Append(String.Format("{0}N", SIFormat((float)SettingsStore.Get(b, "force_slider", DefaultForceMWN))));
            forceSlider.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(forceSlider);

            heightSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("HoverRail_HeightOffset");
            heightSlider.Title = MyStringId.GetOrCompute("Height Offset");
            heightSlider.Tooltip = MyStringId.GetOrCompute("The height we float above the track.");
            heightSlider.SetLimits(0.6f, 1.5f);
            heightSlider.SupportsMultipleBlocks = true;
            heightSlider.Getter = b => (float)SettingsStore.Get(b, "height_target", DefaultHeightM);
            heightSlider.Setter = (b, v) => SettingsStore.Set(b, "height_target", (float)Math.Round(v, 3));
            heightSlider.Writer = (b, result) => result.Append(String.Format("{0}m", (float)SettingsStore.Get(b, "height_target", DefaultHeightM)));
            heightSlider.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(heightSlider);

            damperSlider = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyUpgradeModule>("HoverRail_DampenForce");
            damperSlider.Title = MyStringId.GetOrCompute("Dampen Force");
            damperSlider.Tooltip = MyStringId.GetOrCompute("Reduces oscillation over time.");
            damperSlider.SetLimits(0.1f, 0.8f);
            damperSlider.SupportsMultipleBlocks = true;
            damperSlider.Getter = b => (float)SettingsStore.Get(b, "dampen_force", 0.5f);
            damperSlider.Setter = (b, v) => SettingsStore.Set(b, "dampen_force", (float)Math.Round(v, 1));
            damperSlider.Writer = (b, result) => result.Append(String.Format("{0}", SIFormat((float)SettingsStore.Get(b, "dampen_force", 0.5f))));
            damperSlider.Visible = BlockIsEngine;
            MyAPIGateway.TerminalControls.AddControl<IMyUpgradeModule>(damperSlider);

            lowerHeightAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HoverRailEngine_LowerHeight0.1");
            lowerHeightAction.Name = new StringBuilder("Lower Height");
            lowerHeightAction.Action = LowerHeightAction;
            lowerHeightAction.Writer = (b, builder) =>
            {
                builder.Clear();
                builder.Append(String.Format("{0} -", (float)SettingsStore.Get(b, "height_offset", DefaultHeightM)));
            };
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(lowerHeightAction);

            raiseHeightAction = MyAPIGateway.TerminalControls.CreateAction<IMyUpgradeModule>("HoverRailEngine_RaiseHeight0.1");
            raiseHeightAction.Name = new StringBuilder("Raise Height");
            raiseHeightAction.Action = RaiseHeightAction;
            raiseHeightAction.Writer = (b, builder) =>
            {
                builder.Clear();
                builder.Append(String.Format("{0} +", (float)SettingsStore.Get(b, "height_offset", DefaultHeightM)));
            };
            MyAPIGateway.TerminalControls.AddAction<IMyUpgradeModule>(raiseHeightAction);

            MyAPIGateway.TerminalControls.CustomActionGetter += GetEngineActions;
        }
    }
}
