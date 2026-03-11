using BepInEx;
using BepInEx.Configuration;
using EFT.UI.Ragfair;
using HarmonyLib;
using RefinedFleaListPlugin;
using System.Collections.Generic;
using System.Linq;

namespace RefinedFleaOfferList
{
    [BepInPlugin("ciallo.RefinedFleaOfferList", "Refined Flea Offer List", "1.0.1")]
    public class RefinedFleaListPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Activation;
        public static ConfigEntry<bool> NoGP;
        public static ConfigEntry<int> PageCapacity;
        private static int OriginalPageCount = -1;
        public static ConfigEntry<bool> RetainFull;
        public static ConfigEntry<bool> RetainRub;
        public static ConfigEntry<int> SingleCount;
        public static ConfigEntry<bool> SingleVanilla;

        public static BepInEx.Logging.ManualLogSource Log;
        private Harmony harmony;

        private void Awake()
        {
            Activation = Config.Bind("General", "Activate All", true,
                "Only show the cheapest of each item.\nWill force sorting by price from low to high.");
            NoGP = Config.Bind("General", "No GP Offers", false,
                "Remove all GP coin offers when browsing flea. Can't affect weapon build purchase.");
            PageCapacity = Config.Bind("General", "Page Capacity", 300,
                "How many original offers to process in one flea page\n" +
                "Hidden orders still occupy the count, thus we need a big num to contain more items in one page.\n" +
                "Auto recover to original EFT value when deactivate.");

            RetainFull = Config.Bind("General", "Retain Full-condition Offer", false,
                "Retain cheapest full durability offer when browse >1 type of items\n" +
                "If you want only FullDura offers, use vanilla EFT flea filter.");
            RetainRub = Config.Bind("General", "Retain Ruble Offer", false,
                "Retain cheapest Ruble offer when browse >1 type of items\n" +
                "If you want only Ruble offers, use vanilla EFT flea filter.");

            SingleCount = Config.Bind("General", "Single Item Offers Quantity", 3,
                "When select one specific item, show the N cheapest offers.\n" +
                "Will *2 +2 if all items have no difference.");
            SingleVanilla = Config.Bind("General", "Vanilla Single Item Page", false,
                "When select one specific item, show all offers in original arrangement.");

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

                        if (Activation.Value)
                            offersPerPageProp.SetValue(ragFairClass, PageCapacity.Value);
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
                if (!Activation.Value) return;

                // 取 OfferViewList 的 eviewListType_0 字段
                var viewTypeField = AccessTools.Field(typeof(OfferViewList), "eviewListType_0");
                if (viewTypeField == null) return;

                var viewType = (EViewListType)viewTypeField.GetValue(__instance);
                if (viewType != EViewListType.AllOffers && viewType != EViewListType.WishList) return;

                var ragFairClassField = AccessTools.Field(typeof(OfferViewList), "ragFairClass");
                var ragFairClass = (RagFairClass)ragFairClassField.GetValue(__instance);
                if (ragFairClass == null) return;

                // 先获取全部订单，判断是否单项物品
                var allOffers = ragFairClass.Offers?.ToList() ?? new List<Offer>();
                if (allOffers.Count == 0) return;

                bool singleTemplate = allOffers.Select(o => o.Item.TemplateId).Distinct().Count() == 1;
                if (singleTemplate && SingleVanilla.Value) return;

                // 去除不可用订单、GP币订单
                var offers = allOffers
                    .Where(o => !o.Locked && o.CanBeBought)
                    .ToList();

                if (NoGP.Value)
                {
                    string gpId = "5d235b4d86f7742e017bc88a";
                    offers = offers
                        .Where(o => o.Requirements == null || !o.Requirements.Any(r => r.TemplateId == gpId))
                        .ToList();
                }
                if (offers.Count == 0) return;

                int cheapestCount = singleTemplate
                    ? SingleCount.Value
                    : 1;

                // 若某类物品只有不可用订单则保留
                var groupedByTemplate = allOffers.GroupBy(o => o.Item.TemplateId);
                bool hasUsable;
                foreach (var group in groupedByTemplate)
                {
                    hasUsable = group.Any(o => !o.Locked && o.CanBeBought);
                    if (!hasUsable)
                    {
                        foreach (var blocked in group)
                        {
                            if (!offers.Any(f => f.Id == blocked.Id))
                                offers.Add(blocked);
                        }
                    }
                }

                // 若当前 offers 物品完全相同，多保留一些
                if (singleTemplate)
                {
                    bool allSame = offers
                        .GroupBy(o =>
                        {
                            var parts = o.Item.GetAllVisibleItems()
                                              .Select(p => p.TemplateId.ToString())
                                              .OrderBy(id => id);
                            string buildSignature = string.Join("_", parts);

                            var (current, max) = GetItemState.Get(o.Item);
                            return $"{o.Item.TemplateId}_{buildSignature}_{current}_{max}";
                        })
                        .Count() == 1;

                    if (allSame) cheapestCount = cheapestCount * 2 + 2;
                }

                // 按物品模板+子物品组合分组，保留每组中价格最低的 N 个订单
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
                                      .Take(cheapestCount))
                    .ToList();

                // 额外保留最低价卢布订单
                if (RetainRub.Value || singleTemplate)
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

                // 额外保留最低价满状态订单
                if (RetainFull.Value || singleTemplate)
                {
                    var existingIds = new HashSet<string>(filtered.Select(f => f.Id));

                    // 先计算每个 offer 的状态
                    var offerStates = offers.Select(o =>
                    {
                        var (current, max) = GetItemState.Get(o.Item);
                        return new { Offer = o, Current = current, Max = max };
                    }).ToList();

                    // 按模板 + 子物品结构分组
                    var grouped = offerStates
                        .GroupBy(x => new
                        {
                            x.Offer.Item.TemplateId,
                            SubItems = string.Join(",", x.Offer.Item.GetAllVisibleItems()
                                .Select(i => i.TemplateId)
                                .OrderBy(id => id))
                        });

                    foreach (var group in grouped)
                    {
                        // 找出该组的最大当前值
                        float maxCurrent = group.Max(x => x.Current);

                        var bestOffer = group
                            .Where(x => x.Current == maxCurrent)
                            .OrderBy(x => x.Offer.SummaryCost)
                            .Select(x => x.Offer)
                            .FirstOrDefault();

                        if (bestOffer != null && !existingIds.Contains(bestOffer.Id))
                        {
                            filtered.Add(bestOffer);
                            existingIds.Add(bestOffer.Id);
                        }

                        // 如果需要额外保留卢布订单
                        if (RetainRub.Value || singleTemplate)
                        {
                            var rubId = "5449016a4bdc2d6f028b456f";
                            var bestRubOffer = group
                                .Where(x => x.Current == maxCurrent &&
                                            x.Offer.Requirements != null &&
                                            x.Offer.Requirements.Any(r => r.TemplateId == rubId))
                                .OrderBy(x => x.Offer.SummaryCost)
                                .Select(x => x.Offer)
                                .FirstOrDefault();

                            if (bestRubOffer != null && !existingIds.Contains(bestRubOffer.Id))
                            {
                                filtered.Add(bestRubOffer);
                                existingIds.Add(bestRubOffer.Id);
                            }
                        }
                    }
                }

                // 单物品情况加回不可用订单
                if (singleTemplate)
                {
                    var blockedOffers = ragFairClass.Offers?
                        .Where(o => !o.Locked && o.CanBeBought)
                        .ToList() ?? new List<Offer>();

                    if (blockedOffers.Count > 0)
                    {
                        foreach (var bo in blockedOffers)
                        {
                            if (!filtered.Any(f => f.Id == bo.Id))
                                filtered.Add(bo);
                        }
                    }
                }

                // 最后按价格排序
                if (RetainRub.Value || RetainFull.Value || singleTemplate)
                    filtered = filtered.OrderBy(o => o.SummaryCost).ToList();

                ragFairClass.ClearOffers();
                foreach (var offer in filtered)
                    ragFairClass.Offers.Add(offer);
            }
        }
    }
}
