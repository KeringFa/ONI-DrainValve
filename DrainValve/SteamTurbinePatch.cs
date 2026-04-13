using HarmonyLib;
using UnityEngine;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DrainValve
{
    [HarmonyPatch(typeof(SteamTurbineConfig2), nameof(SteamTurbineConfig2.DoPostConfigureComplete))]
    public class SteamTurbineConfigPatch
    {
        private const float STORAGE_CAPACITY_KG = 200f;

        public static void Postfix(GameObject go)
        {
            if (go == null) return;

            var turbine = go.GetComponent<SteamTurbine>();
            if (turbine != null && !SteamTurbineEnergySimPatch.InitFailed)
            {
                var gasStorage = SteamTurbineEnergySimPatch.GetGasStorage(turbine);
                var liquidStorage = SteamTurbineEnergySimPatch.GetLiquidStorage(turbine);
                if (gasStorage != null) gasStorage.capacityKg = STORAGE_CAPACITY_KG;
                if (liquidStorage != null) liquidStorage.capacityKg = STORAGE_CAPACITY_KG;
            }
            else
            {
                var storages = go.GetComponents<Storage>();
                if (storages.Length >= 2)
                {
                    storages[0].capacityKg = STORAGE_CAPACITY_KG;
                    storages[1].capacityKg = STORAGE_CAPACITY_KG;
                }
            }

            go.AddOrGet<ConfigPanel>();
        }
    }

    [HarmonyPatch(typeof(SteamTurbine), "EnergySim200ms")]
    public class SteamTurbineEnergySimPatch
    {
        private const float MIN_STEAM_MASS = 0.1f;
        private const float BUFFER_EPSILON = 0.001f;
        private const byte MAX_DISEASE_COUNT = byte.MaxValue;
        private const string DISEASE_MODIFY_REASON = "SteamTurbine.EnergySim200ms";

        private static AccessTools.FieldRef<SteamTurbine, Storage> gasStorageRef;
        private static AccessTools.FieldRef<SteamTurbine, Storage> liquidStorageRef;
        private static AccessTools.FieldRef<SteamTurbine, HandleVector<int>.Handle> structureTemperatureRef;
        private static AccessTools.FieldRef<SteamTurbine, HandleVector<int>.Handle> accumulatorRef;
        private static AccessTools.FieldRef<SteamTurbine, MeterController> meterRef;
        private static AccessTools.FieldRef<SteamTurbine, float> lastSampleTimeRef;
        private static AccessTools.FieldRef<SteamTurbine, float> storedMassRef;
        private static AccessTools.FieldRef<SteamTurbine, float> storedTemperatureRef;

        private static HashedString tintSymbol;
        private static Operational.Flag wireConnectedFlag;
        private static bool hasWireConnectedFlag;

        private static Func<SteamTurbine, PrimaryElement, float> joulesToGenerateFunc;
        private static Func<SteamTurbine, PrimaryElement, float> heatFromCoolingSteamFunc;
        private static Action<Generator> checkConnectionFunc;

        private static bool hasStoredMassFields;
        private static bool initFailed;

        private static readonly ConditionalWeakTable<SteamTurbine, ConfigPanel> configPanelCache = new();
        private static readonly Dictionary<SimHashes, Tag> elementTagCache = new();

        internal static bool InitFailed => initFailed;

        internal static Storage GetGasStorage(SteamTurbine turbine) =>
            gasStorageRef != null ? gasStorageRef(turbine) : null;

        internal static Storage GetLiquidStorage(SteamTurbine turbine) =>
            liquidStorageRef != null ? liquidStorageRef(turbine) : null;

        static SteamTurbineEnergySimPatch()
        {
            var nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
            var allAccess = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var staticNonPublic = BindingFlags.Static | BindingFlags.NonPublic;
            var staticAll = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                var tintField = typeof(SteamTurbine).GetField("TINT_SYMBOL", staticNonPublic);
                tintSymbol = tintField != null ? (HashedString)tintField.GetValue(null) : new HashedString("meter_fill");

                var wireField = typeof(Generator).GetField("wireConnectedFlag", staticAll);
                if (wireField != null)
                {
                    wireConnectedFlag = (Operational.Flag)wireField.GetValue(null);
                    hasWireConnectedFlag = true;
                }

                gasStorageRef = AccessTools.FieldRefAccess<SteamTurbine, Storage>("gasStorage");
                liquidStorageRef = AccessTools.FieldRefAccess<SteamTurbine, Storage>("liquidStorage");
                structureTemperatureRef = AccessTools.FieldRefAccess<SteamTurbine, HandleVector<int>.Handle>("structureTemperature");
                accumulatorRef = AccessTools.FieldRefAccess<SteamTurbine, HandleVector<int>.Handle>("accumulator");
                meterRef = AccessTools.FieldRefAccess<SteamTurbine, MeterController>("meter");
                lastSampleTimeRef = AccessTools.FieldRefAccess<SteamTurbine, float>("lastSampleTime");

                joulesToGenerateFunc = (Func<SteamTurbine, PrimaryElement, float>)Delegate.CreateDelegate(
                    typeof(Func<SteamTurbine, PrimaryElement, float>),
                    null,
                    typeof(SteamTurbine).GetMethod("JoulesToGenerate", allAccess));

                heatFromCoolingSteamFunc = (Func<SteamTurbine, PrimaryElement, float>)Delegate.CreateDelegate(
                    typeof(Func<SteamTurbine, PrimaryElement, float>),
                    null,
                    typeof(SteamTurbine).GetMethod("HeatFromCoolingSteam", allAccess));

                var checkMethod = typeof(Generator).GetMethod("CheckConnectionStatus", nonPublic);
                if (checkMethod != null)
                {
                    checkConnectionFunc = (Action<Generator>)Delegate.CreateDelegate(
                        typeof(Action<Generator>), null, checkMethod);
                }

                initFailed = false;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DrainValve] Failed to initialize delegates, falling back to vanilla: {e.Message}");
                initFailed = true;
            }

            try
            {
                storedMassRef = AccessTools.FieldRefAccess<SteamTurbine, float>("storedMass");
                storedTemperatureRef = AccessTools.FieldRefAccess<SteamTurbine, float>("storedTemperature");
                hasStoredMassFields = true;
            }
            catch
            {
                hasStoredMassFields = false;
            }
        }

        public static bool Prefix(SteamTurbine __instance, float dt)
        {
            if (initFailed) return true;

            var configPanel = GetConfigPanel(__instance);
            float threshold = (configPanel != null) ? configPanel.drainThreshold : 0f;

            if (threshold <= 0f)
            {
                if (configPanel != null && configPanel.BufferedMass > 0f)
                {
                    ReleaseBufferedWater(__instance, configPanel);
                }
                return true;
            }

            ProcessSteamTurbine(__instance, dt, threshold, configPanel);
            return false;
        }

        private static ConfigPanel GetConfigPanel(SteamTurbine turbine)
        {
            if (!configPanelCache.TryGetValue(turbine, out var panel))
            {
                panel = turbine.GetComponent<ConfigPanel>();
                if (panel != null)
                    configPanelCache.Add(turbine, panel);
            }
            return panel;
        }

        private static Tag GetSourceTag(SimHashes srcElem)
        {
            if (!elementTagCache.TryGetValue(srcElem, out var tag))
            {
                tag = ElementLoader.FindElementByHash(srcElem).tag;
                elementTagCache[srcElem] = tag;
            }
            return tag;
        }

        private static void ReleaseBufferedWater(SteamTurbine turbine, ConfigPanel configPanel)
        {
            var liquidStorage = liquidStorageRef(turbine);
            if (liquidStorage == null) return;

            liquidStorage.AddLiquid(turbine.destElem, configPanel.BufferedMass,
                turbine.outputElementTemperature, MAX_DISEASE_COUNT, 0, true, true);

            configPanel.BufferedMass = 0f;
        }

        private static void AbsorbStoredSteam(SteamTurbine turbine, Storage gasStorage)
        {
            if (!hasStoredMassFields || gasStorage == null) return;

            float storedMass = storedMassRef(turbine);
            if (storedMass > 0f)
            {
                float storedTemp = storedTemperatureRef(turbine);
                gasStorage.AddGasChunk(turbine.srcElem, storedMass, storedTemp,
                    MAX_DISEASE_COUNT, 0, true, true);
                storedMassRef(turbine) = 0f;
                storedTemperatureRef(turbine) = 0f;
            }
        }

        private static void ProcessSteamTurbine(SteamTurbine turbine, float dt, float threshold, ConfigPanel configPanel)
        {
            var operational = configPanel.operational;
            var meter = meterRef(turbine);

            checkConnectionFunc?.Invoke(turbine);
            if (hasWireConnectedFlag)
                operational.SetFlag(wireConnectedFlag, turbine.CircuitID != ushort.MaxValue);

            if (!operational.IsOperational)
            {
                meter?.SetPositionPercent(0f);
                if (configPanel.BufferedMass > 0f)
                {
                    ReleaseBufferedWater(turbine, configPanel);
                }
                return;
            }

            var gasStorage = gasStorageRef(turbine);
            var liquidStorage = liquidStorageRef(turbine);

            AbsorbStoredSteam(turbine, gasStorage);

            var (generatedJoules, steamFound) = ProcessSteam(turbine, dt, gasStorage, configPanel);

            FlushBuffer(turbine, threshold, configPanel, liquidStorage, !steamFound);

            UpdatePowerAndDisplay(turbine, generatedJoules, meter);
        }

        private static (float generatedJoules, bool steamFound) ProcessSteam(
            SteamTurbine turbine, float dt, Storage gasStorage, ConfigPanel configPanel)
        {
            float generatedJoules = 0f;
            bool steamFound = false;

            if (gasStorage == null || gasStorage.items.Count == 0)
                return (generatedJoules, steamFound);

            var srcTag = GetSourceTag(turbine.srcElem);
            GameObject steamGo = gasStorage.FindFirst(srcTag);

            if (steamGo == null)
                return (generatedJoules, steamFound);

            PrimaryElement steamElement = steamGo.GetComponent<PrimaryElement>();
            if (steamElement == null)
                return (generatedJoules, steamFound);

            if (steamElement.Mass > MIN_STEAM_MASS)
            {
                steamFound = true;
                float massToProcess = Mathf.Min(steamElement.Mass, turbine.pumpKGRate * dt);

                generatedJoules = Mathf.Min(
                    joulesToGenerateFunc(turbine, steamElement) * (massToProcess / turbine.pumpKGRate),
                    turbine.WattageRating * dt);

                float heatFromCooling = heatFromCoolingSteamFunc(turbine, steamElement);
                float heatRemovedDTU = heatFromCooling * (massToProcess / steamElement.Mass);

                steamElement.Mass -= massToProcess;
                steamElement.ModifyDiseaseCount(-steamElement.DiseaseCount, DISEASE_MODIFY_REASON);

                float lastSampleTime = lastSampleTimeRef(turbine);
                float display_dt = (lastSampleTime > 0f) ? (Time.time - lastSampleTime) : 1f;
                lastSampleTimeRef(turbine) = Time.time;

                var structTemp = structureTemperatureRef(turbine);
                GameComps.StructureTemperatures.ProduceEnergy(structTemp,
                    heatRemovedDTU * turbine.wasteHeatToTurbinePercent,
                    "SteamTurbine2_HeatSource", display_dt);

                configPanel.BufferedMass += massToProcess;
            }
            else if (steamElement.Mass > 0f)
            {
                steamFound = true;
                configPanel.BufferedMass += steamElement.Mass;
                steamElement.Mass = 0f;
            }

            return (generatedJoules, steamFound);
        }

        private static void FlushBuffer(SteamTurbine turbine, float threshold,
            ConfigPanel configPanel, Storage liquidStorage, bool noSteamFound)
        {
            if (liquidStorage == null) return;

            if (configPanel.BufferedMass >= threshold)
            {
                liquidStorage.AddLiquid(turbine.destElem, threshold,
                    turbine.outputElementTemperature, MAX_DISEASE_COUNT, 0, true, true);
                configPanel.BufferedMass -= threshold;
            }

            if (noSteamFound && configPanel.BufferedMass > 0f)
            {
                liquidStorage.AddLiquid(turbine.destElem, configPanel.BufferedMass,
                    turbine.outputElementTemperature, MAX_DISEASE_COUNT, 0, true, true);
                configPanel.BufferedMass = 0f;
            }
        }

        private static void UpdatePowerAndDisplay(SteamTurbine turbine, float generatedJoules, MeterController meter)
        {
            var accumulator = accumulatorRef(turbine);
            Game.Instance.accumulators.Accumulate(accumulator, generatedJoules);

            if (generatedJoules > 0f)
            {
                turbine.GenerateJoules(generatedJoules, false);
            }

            float averageRate = Game.Instance.accumulators.GetAverageRate(accumulator);
            float percent = averageRate / turbine.WattageRating;
            meter?.SetPositionPercent(percent);
            meter?.SetSymbolTint(tintSymbol, Color.Lerp(Color.red, Color.green, percent));
        }
    }
}
