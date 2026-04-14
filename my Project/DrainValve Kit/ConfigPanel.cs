using KSerialization;
using UnityEngine;

namespace DrainValve
{
    [SerializationConfig(MemberSerialization.OptIn)]
    public class ConfigPanel : KMonoBehaviour, ISingleSliderControl, ISliderControl
    {
        public const float SLIDER_MAX = 10f;
        public const float SLIDER_MIN = 0f;
        public const float DEFAULT_BATCH = 2f;

        private static readonly EventSystem.IntraObjectHandler<ConfigPanel> OnCopySettingsDelegate =
            new(OnCopySettings);

        private static void OnCopySettings(ConfigPanel comp, object data)
        {
            comp.OnCopySettings(data);
        }

        [MyCmpReq]
        public SteamTurbine steamTurbine;

        [MyCmpReq]
        public Operational operational;

        [MyCmpAdd]
        public CopyBuildingSettings copyBuildingSettings;

        [Serialize]
        public float drainThreshold = -1f;

        [Serialize]
        public volatile float BufferedMass;

        public int SliderDecimalPlaces(int i) => 1;

        public float GetSliderMin(int i) => SLIDER_MIN;

        public float GetSliderMax(int i) => SLIDER_MAX;

        public float GetSliderValue(int i) => drainThreshold;

        public string GetSliderTooltipKey(int i) => "DrainValve.STRINGS.UI.UISIDESCREENS.DRAINVALVECONFIG.TOOLTIP";

        public string GetSliderTooltip(int index) => STRINGS.UI.UISIDESCREENS.DRAINVALVECONFIG.TOOLTIP;

        public string SliderTitleKey => "DrainValve.STRINGS.UI.UISIDESCREENS.DRAINVALVECONFIG.TITLE";

        public string SliderUnits => "kg";

        public void SetSliderValue(float val, int index)
        {
            drainThreshold = val;
        }

        protected override void OnPrefabInit()
        {
            base.OnPrefabInit();
            Subscribe<ConfigPanel>((int)GameHashes.CopySettings, OnCopySettingsDelegate);
        }

        protected override void OnSpawn()
        {
            base.OnSpawn();
            if (drainThreshold < 0f || drainThreshold > SLIDER_MAX)
            {
                drainThreshold = DEFAULT_BATCH;
            }
        }

        protected override void OnCleanUp()
        {
            if (BufferedMass > 0f && steamTurbine is not null)
            {
                if (TryReleaseBufferedWater())
                {
                    BufferedMass = 0f;
                }
                else
                {
                    Debug.LogWarning("[DrainValve] Failed to release buffered water on cleanup");
                }
            }
            base.OnCleanUp();
        }

        private bool TryReleaseBufferedWater()
        {
            int cell = Grid.PosToCell(this);
            if (cell != -1)
            {
                Element element = ElementLoader.FindElementByHash(steamTurbine.destElem);
                if (element != null)
                {
                    element.substance.SpawnResource(Grid.CellToPosCCC(cell, 0f),
                        BufferedMass, steamTurbine.outputElementTemperature,
                        byte.MaxValue, 0, true, false);
                    return true;
                }
            }

            foreach (var storage in GetComponents<Storage>())
            {
                if (storage != null)
                {
                    storage.AddLiquid(steamTurbine.destElem, BufferedMass,
                        steamTurbine.outputElementTemperature, byte.MaxValue, 0, true, true);
                    return true;
                }
            }

            return false;
        }

        internal void OnCopySettings(object data)
        {
            if (data is GameObject go)
            {
                ConfigPanel comp = go.GetComponent<ConfigPanel>();
                if (comp != null)
                {
                    drainThreshold = comp.drainThreshold;
                }
            }
        }
    }
}
