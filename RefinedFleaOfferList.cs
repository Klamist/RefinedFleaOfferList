using BepInEx;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using EFT.UI.Ragfair;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;

namespace RefinedFleaOfferList
{
    [BepInPlugin("ciallo.RefinedFleaOfferList", "Refined Flea Offer List", "1.0.0")]
    public class RefinedFleaListPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Thinner;
        public static ConfigEntry<int> CheapestCount;
        public static ConfigEntry<int> PageCount;
        private static int OriginalPageCount = -1;
        public static ConfigEntry<bool> SingleShowAll;
        public static ConfigEntry<bool> AlwaysShowRub;
        public static ConfigEntry<bool> ShowFullDura;

        public static BepInEx.Logging.ManualLogSource Log;
        private Harmony harmony;

        private void Awake()
        {
            Thinner = Config.Bind("General", "Activation", true,
                "Only show N cheapest of each item.\nWill force sorting by price from low to high.");
            CheapestCount = Config.Bind("General", "Offer Number Per Item", 1, "How many cheapest offers to show.");
            PageCount = Config.Bind("General", "Page Capacity (?)", 300,
                "Cover vanilla setting for how many offers per flea page\n" +
                "Hidden orders still occupy the count, need a big num to contain more items in one page.\n" +
                "Auto recover to original EFT value when deactivate.");
            SingleShowAll = Config.Bind("General", "Single Item Show All (?)", false,
                "Show all offers when select one specific item.");
            AlwaysShowRub = Config.Bind("General", "Show Ruble Offer too (?)", true,
                "Additionally show cheapest Ruble offer.\n" +
                "If you want only Ruble offers, use EFT flea filter.");
            ShowFullDura = Config.Bind("General", "Show Full Durability too (?)", true,
                "Additionally show cheapest full durability offer.\n" +
                "If you want only FullDura offers, use EFT flea filter.");

            Log = Logger;
            harmony = new Harmony("ciallo.RefinedFleaOfferList");
            harmony.PatchAll();
        }

        private void OnDisable()
        {
            harmony?.UnpatchSelf();
        }

        // 设置分页上限为指定值，若不启用脚本则改为原值
        [HarmonyPatch(typeof(OfferViewList), "method_0")]
        class Patch_OfferViewList_method_0
        {
            static void Prefix(OfferViewList __instance)
            {
                var ragFairClassField = AccessTools.Field(typeof(OfferViewList), "ragFairClass");
                var ragFairClass = (RagFairClass)ragFairClassField.GetValue(__instance);
                if (ragFairClass != null)
                {
                    var offersPerPageProp = AccessTools.Property(typeof(RagFairClass), "OffersPerPageCount");
                    if (offersPerPageProp != null)
                    {
                        int currentValue = (int)offersPerPageProp.GetValue(ragFairClass);

                        if (OriginalPageCount == -1)
                            OriginalPageCount = currentValue;

                        if (RefinedFleaListPlugin.Thinner.Value)
                            offersPerPageProp.SetValue(ragFairClass, RefinedFleaListPlugin.PageCount.Value);
                        else
                            offersPerPageProp.SetValue(ragFairClass, OriginalPageCount);
                    }
                }
            }
        }

        // 按物品模板+子物品组合去重
        [HarmonyPatch(typeof(OfferViewList), "method_14")]
        class Patch_OfferViewList_method_14
        {
            static void Postfix(OfferViewList __instance)
            {
                if (!RefinedFleaListPlugin.Thinner.Value) return;

                // 取 OfferViewList 的 eviewListType_0 字段
                var viewTypeField = AccessTools.Field(typeof(OfferViewList), "eviewListType_0");
                if (viewTypeField == null) return;

                var viewType = (EViewListType)viewTypeField.GetValue(__instance);
                if (viewType != EViewListType.AllOffers && viewType != EViewListType.WishList) return;

                var ragFairClassField = AccessTools.Field(typeof(OfferViewList), "ragFairClass");
                var ragFairClass = (RagFairClass)ragFairClassField.GetValue(__instance);
                if (ragFairClass == null) return;

                var offers = ragFairClass.Offers?.ToList();
                if (offers == null || offers.Count == 0) return;

                bool singleTemplate = offers.Select(o => o.Item.TemplateId).Distinct().Count() == 1;
                if (singleTemplate && RefinedFleaListPlugin.SingleShowAll.Value)
                    return;

                var filtered = offers
                    .GroupBy(o =>
                    {
                        var parts = o.Item.GetAllVisibleItems()
                                          .Select(p => p.TemplateId.ToString())
                                          .OrderBy(id => id);
                        string buildSignature = string.Join("_", parts);
                        return $"{o.Item.TemplateId}_{buildSignature}";
                    })
                    .SelectMany(g => g.OrderBy(o => o.SummaryCost)
                                      .Take(RefinedFleaListPlugin.CheapestCount.Value))
                    .ToList();

                // 额外保留最低价卢布订单并重新排序
                if (RefinedFleaListPlugin.AlwaysShowRub.Value)
                {
                    var rubId = "5449016a4bdc2d6f028b456f";
                    var rubOffers = offers
                        .Where(o => o.Requirements != null &&
                                    o.Requirements.Any(r => r.TemplateId == rubId))
                        .GroupBy(o => o.Item.TemplateId.ToString())
                        .Select(g => g.OrderBy(o => o.SummaryCost).FirstOrDefault())
                        .Where(o => o != null);

                    var existingIds = new HashSet<string>(filtered.Select(f => f.Id));
                    foreach (var rubOffer in rubOffers)
                    {
                        if (!existingIds.Contains(rubOffer.Id))
                        {
                            filtered.Add(rubOffer);
                            existingIds.Add(rubOffer.Id);
                        }
                    }
                }

                // 额外保留最低价满耐久订单并重新排序
                if (RefinedFleaListPlugin.ShowFullDura.Value)
                {
                    var existingIds = new HashSet<string>(filtered.Select(f => f.Id));
                    var fullDuraOffer = offers
                        .Select(o =>
                        {
                            var rep = o.Item.GetItemComponent<RepairableComponent>();
                            if (rep == null)
                            {
                                return null;
                            }
                            return (rep.MaxDurability == 100 && rep.Durability == rep.MaxDurability) ? o : null;
                        })
                        .Where(o => o != null)
                        .OrderBy(o => o.SummaryCost)
                        .FirstOrDefault();

                    if (fullDuraOffer != null && !existingIds.Contains(fullDuraOffer.Id))
                    {
                        filtered.Add(fullDuraOffer);
                        existingIds.Add(fullDuraOffer.Id);
                    }

                    // 如果这条不是卢布订单，再找最便宜满耐久且卢布的订单（同样要求100/100）
                    var rubId = "5449016a4bdc2d6f028b456f";
                    bool isRub = fullDuraOffer != null &&
                                 fullDuraOffer.Requirements != null &&
                                 fullDuraOffer.Requirements.Any(r => r.TemplateId == rubId);

                    if (!isRub && RefinedFleaListPlugin.AlwaysShowRub.Value)
                    {
                        var fullDuraRubOffer = offers
                            .Select(o =>
                            {
                                var rep = o.Item.GetItemComponent<RepairableComponent>();
                                return (rep != null && rep.MaxDurability == 100 && rep.Durability == rep.MaxDurability &&
                                        o.Requirements != null && o.Requirements.Any(r => r.TemplateId == rubId)) ? o : null;
                            })
                            .Where(o => o != null)
                            .OrderBy(o => o.SummaryCost)
                            .FirstOrDefault();

                        if (fullDuraRubOffer != null && !existingIds.Contains(fullDuraRubOffer.Id))
                        {
                            filtered.Add(fullDuraRubOffer);
                            existingIds.Add(fullDuraRubOffer.Id);
                        }
                    }
                }

                if(RefinedFleaListPlugin.AlwaysShowRub.Value || RefinedFleaListPlugin.ShowFullDura.Value)
                    filtered = filtered.OrderBy(o => o.SummaryCost).ToList();

                ragFairClass.ClearOffers();
                foreach (var offer in filtered)
                    ragFairClass.Offers.Add(offer);
            }
        }
    }
}
