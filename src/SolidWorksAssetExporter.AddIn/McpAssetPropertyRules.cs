using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SolidWorksAssetExporter.Core;

namespace SolidWorksAssetExporter.AddIn
{
    internal static class McpAssetPropertyRules
    {
        private static readonly string[] Flags = { PropertyRules.IsAsset, PropertyRules.IsWorkstation,
            PropertyRules.IsTool, PropertyRules.IsFixture, PropertyRules.CanContain, PropertyRules.IsQuickChanger,
            PropertyRules.IsQuickChangerRack, PropertyRules.IsAdjustable, PropertyRules.HasQrCode };
        private static readonly string[] Integers = { PropertyRules.SlotsNum, "QR_num", PropertyRules.AssetVersion };
        internal static readonly string[] Fields = Flags.Concat(Integers).Concat(new[]
        {
            PropertyRules.AssetClass, PropertyRules.PartName, PropertyRules.DesignPurpose, PropertyRules.WorkstationName,
            PropertyRules.QuickChangerSide, PropertyRules.ConnectionInterface, PropertyRules.AcceptsInterfaces,
            PropertyRules.SlotType, PropertyRules.AssetEngagement,
            "QR_size", "QR_spacing", "设计原理", "升版说明"
        }).ToArray();

        internal static Dictionary<string, string> Normalize(IDictionary<string, object> supplied)
        {
            if (supplied.Count == 0) throw new ValidationException("properties 至少需要一个属性。");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in supplied)
            {
                var name = Fields.FirstOrDefault(field => field.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (name == null) throw new ValidationException("不支持填写属性 [" + pair.Key + "]。");
                if (result.ContainsKey(name)) throw new ValidationException("重复填写属性 [" + name + "]。");
                result.Add(name, NormalizeValue(name, pair.Value));
            }
            return result;
        }

        internal static Dictionary<string, string> ContentsInterfaceCleanup(InterfacePropertySnapshot snapshot,
            IDictionary<string, string> requested)
        {
            var cleanup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in new[] { PropertyRules.AcceptsInterfaces, PropertyRules.LegacyAcceptsInterface })
            {
                if (requested.ContainsKey(field)) continue;
                var names = InterfaceEditRules.Split(snapshot.Get(field));
                if (names.Any(PropertyRules.IsContentsInterface))
                    cleanup.Add(field, string.Join(";", names.Where(name => !PropertyRules.IsContentsInterface(name))));
            }
            if (!requested.ContainsKey(PropertyRules.ConnectionInterface))
            {
                var names = InterfaceEditRules.Split(snapshot.Get(PropertyRules.ConnectionInterface));
                if (names.Count != 0 && names.All(PropertyRules.IsContentsInterface))
                    cleanup.Add(PropertyRules.ConnectionInterface, "");
            }
            return cleanup;
        }

        internal static void ApplySlotDefinition(InterfacePropertySnapshot snapshot,
            IDictionary<string, string> requested, IDictionary<string, string> proposed, bool useDefaults)
        {
            var capacity = PropertyRules.ReadSlotCapacity(proposed);
            if (!(capacity > 0) && requested.ContainsKey(PropertyRules.SlotType))
                throw new ValidationException("仅当文件级 [slots_num] 为正整数时才可填写 [slot_type]。");
            if (capacity == 0 && requested.ContainsKey(PropertyRules.SlotsNum) &&
                !string.IsNullOrWhiteSpace(snapshot.Get(PropertyRules.SlotType)))
                SetSlotProperty(requested, proposed, PropertyRules.SlotType, "");
            if (!useDefaults) return;

            string engagement;
            if (!proposed.TryGetValue(PropertyRules.AssetEngagement, out engagement) || string.IsNullOrWhiteSpace(engagement))
            {
                engagement = PropertyRules.AssetEngagementValues[0];
                SetSlotProperty(requested, proposed, PropertyRules.AssetEngagement, engagement);
            }
            if (capacity > 0 && engagement.Trim() == "loose" && PropertyRules.ReadAssetSlotType(proposed).Length == 0)
                SetSlotProperty(requested, proposed, PropertyRules.SlotType, PropertyRules.AssetSlotTypes[0]);
        }

        private static void SetSlotProperty(IDictionary<string, string> requested, IDictionary<string, string> proposed,
            string name, string value)
        {
            requested[name] = value;
            proposed[name] = value;
        }

        private static string NormalizeValue(string name, object supplied)
        {
            var value = supplied as string;
            if (Flags.Contains(name))
            {
                var flag = ParseFlag(supplied is bool ? (bool)supplied ? "1" : "0" : NumberOrString(supplied), name);
                if (!flag.HasValue) throw new ValidationException("属性 [" + name + "] 需要明确的 1 或 0。");
                return flag.Value ? "1" : "0";
            }
            if (Integers.Contains(name))
            {
                int number;
                if (!int.TryParse(NumberOrString(supplied), NumberStyles.None, CultureInfo.InvariantCulture, out number) ||
                    (name == PropertyRules.AssetVersion && number == 0))
                    throw new ValidationException("属性 [" + name + "] 的整数值无效。");
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (name == "QR_spacing")
            {
                decimal spacing;
                if (!decimal.TryParse(NumberOrString(supplied), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out spacing) ||
                    spacing < 0) throw new ValidationException("QR_spacing 必须为非负数。");
                return spacing.ToString(CultureInfo.InvariantCulture);
            }
            if (value == null || value.Any(char.IsControl))
                throw new ValidationException("属性 [" + name + "] 必须为单行文本。");
            value = value.Trim();
            if (name == PropertyRules.AssetClass && value != "" && value != "movable" && value != "station" &&
                value != "structure" && value != "robot")
                throw new ValidationException("class 必须为 movable、station、structure 或 robot。");
            if (name == PropertyRules.QuickChangerSide && value != "" && value != "robot_side" && value != "tool_side")
                throw new ValidationException("quick_changer_side 必须为 robot_side、tool_side 或空值。");
            if (name == PropertyRules.SlotType && !PropertyRules.AssetSlotTypes.Contains(value, StringComparer.Ordinal))
                throw new ValidationException("slot_type 必须为 " + string.Join("、", PropertyRules.AssetSlotTypes) + " 中的一项。");
            if (name == PropertyRules.AssetEngagement && !PropertyRules.AssetEngagementValues.Contains(value, StringComparer.Ordinal))
                throw new ValidationException("engagement 必须为 " + string.Join("、", PropertyRules.AssetEngagementValues) + " 中的一项。");
            if (name == PropertyRules.ConnectionInterface && value.Length != 0) return InterfaceEditRules.MechanicalName(value);
            if (name == PropertyRules.AcceptsInterfaces)
                return string.Join(";", InterfaceEditRules.Suggest("", InterfaceEditRules.Split(value)));
            return value;
        }

        private static string NumberOrString(object value)
        {
            if (value is string) return ((string)value).Trim();
            return value is int || value is long || value is decimal || value is double
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        internal static bool? ParseFlag(string value, string name)
        {
            value = (value ?? "").Trim();
            if (value.Length == 0) return null;
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ValidationException("属性 [" + name + "] 的布尔值无效。");
        }

        internal static bool? WorkstationFlag(IDictionary<string, string> properties)
        {
            string current, legacy;
            properties.TryGetValue(PropertyRules.IsWorkstation, out current);
            properties.TryGetValue(PropertyRules.LegacyIsWorkstation, out legacy);
            var flag = ParseFlag(current, PropertyRules.IsWorkstation);
            var oldFlag = ParseFlag(legacy, PropertyRules.LegacyIsWorkstation);
            if (flag.HasValue && oldFlag.HasValue && flag != oldFlag)
                throw new ValidationException("工位新旧标记不一致，请统一文件级属性。");
            return flag ?? oldFlag;
        }
    }
}
