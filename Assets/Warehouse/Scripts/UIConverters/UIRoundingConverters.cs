using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Templates.IndustryFundamentals.UIConverters
{
    public static class UIRoundingConverters
    {
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        public static void Register()
        {
            if (!ConverterGroups.TryGetConverterGroup("RoundToInt", out _))
            {
                var group = new ConverterGroup("RoundToInt");
                group.AddConverter((ref float v) => Mathf.RoundToInt(v).ToString());
                group.AddConverter((ref double v) => Math.Round(v).ToString());
                ConverterGroups.RegisterConverterGroup(group);
            }

            if (!ConverterGroups.TryGetConverterGroup("TwoDecimals", out _))
            {
                var group = new ConverterGroup("TwoDecimals");
                group.AddConverter((ref float v) => v.ToString("F2"));
                group.AddConverter((ref double v) => v.ToString("F2"));
                ConverterGroups.RegisterConverterGroup(group);
            }
        
            if (!ConverterGroups.TryGetConverterGroup("ToPercentageDirect", out _))
            {
                var group = new ConverterGroup("ToPercentageDirect");
                group.AddConverter((ref float v) => $"{Mathf.RoundToInt(v)}%");
                group.AddConverter((ref double v) => $"{Math.Round(v)}%");
                ConverterGroups.RegisterConverterGroup(group);
            }
        
            if (!ConverterGroups.TryGetConverterGroup("ToPercentageX100", out _))
            {
                var group = new ConverterGroup("ToPercentageX100");
                group.AddConverter((ref float v) => $"{Mathf.RoundToInt(v * 100)}%");
                group.AddConverter((ref double v) => $"{Math.Round(v * 100)}%");
                ConverterGroups.RegisterConverterGroup(group);
            }
        }
    }
}