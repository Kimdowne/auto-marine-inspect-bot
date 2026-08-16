using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Unity.Templates.IndustryFundamentals.UIConverters
{
    //[CreateAssetMenu(fileName = "TextConverter", menuName = "Industrial Dashboard/UI/Text Converter")]
    public class TextConverter : ScriptableObject
    {
#if UNITY_EDITOR
        [InitializeOnLoadMethod]
#else
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        public static void RegisterConverters()
        {
            string converterName = "Int To \"Variant X\"";
            if (!ConverterGroups.TryGetConverterGroup(converterName, out _))
            {
                ConverterGroup group = new(converterName);
                group.AddConverter((ref int n) => $"VARIANT {++n}");
                ConverterGroups.RegisterConverterGroup(group);
            }
        }
    }
}