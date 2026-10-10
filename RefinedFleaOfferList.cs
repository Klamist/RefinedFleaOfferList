using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.UI.Ragfair;
using HarmonyLib;
using RefinedFleaListPlugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RefinedFleaOfferList
{
    [BepInPlugin("ciallo.RefinedFleaOfferList", "Refined Flea Offer List", "1.2.0")]
    public class RefinedFleaListPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Activation;
        public static ConfigEntry<bool> NoGP;
        public static ConfigEntry<bool> HideDis;
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
            HideDis = Config.Bind("General", "Hide Disabled Offers", false,
                "Remove all disabled offers on page, unless there has too few or only disabled offers");
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

        void OnDestroy()
        {
            harmony?.UnpatchSelf();
        }

        public static bool IsOfferUsable(Offer o, RagFair ragfair, string profileId, int delaySinceOfferAdd)
        {
            if (o == null) return false;

            // ---- flag2 部分（时间/过期） ----
            bool flag2 = !o.Expired;

            // ---- 基础 flag 部分 ----
            bool flag = !o.NotAvailable;

            // 是否是自己的订单（OfferNotAvailable）
            bool isMyOffer = o.User != null && !string.IsNullOrEmpty(profileId) && profileId == o.User.Id;
            if (isMyOffer) flag = false;

            // 无库存
            if (o.TotalItemCount <= 0) flag = false;

            // 个人限购
            if (o.LimitsReached) flag = false;

            // 被任务锁定
            if (o.Locked) flag = false;

            // ---- 商人订单特殊判断 ----
            bool isTraderOffer = !o.NotAvailable && o.MemberType == EMemberCategory.Trader;
            if (isTraderOffer)
            {
                Profile.TraderInfo trader = null;
                try
                {
                    var profile = Singleton<Profile>.Instance; // 视你的 EFT 版本，也可能是其他方式取 Profile
                    if (profile != null && profile.TradersInfo != null && o.User != null)
                        profile.TradersInfo.TryGetValue(o.User.Id, out trader);
                }
                catch { /* 拿不到就当不可用 */ }

                if (trader == null)
                {
                    flag = false;
                }
                else
                {
                    if (!trader.Available) flag = false;                              // 商人未解锁
                    if (trader.LoyaltyLevel < o.LoyaltyLevel) flag = false;           // 忠诚度/威望不够
                }
            }
            else
            {
                // ---- 玩家订单：挂单延迟 ----
                var creationTime = DateTimeExtensions.UtcNow - o.StartTime;
                if (creationTime.TotalSeconds < delaySinceOfferAdd) flag2 = false;
            }

            return flag && flag2;
        }

        // 设置分页上限为指定值，若不启用脚本则改为原值
        [HarmonyPatch(typeof(OfferViewList), "ManageOffers")]
        class Patch_OfferViewList_ManageOffers
        {
            static void Prefix(OfferViewList __instance)
            {
                var _ragfairField = AccessTools.Field(typeof(OfferViewList), "_ragfair");
                var _ragfair = (RagFair)_ragfairField.GetValue(__instance);
                if (_ragfair != null)
                {
                    var offersPerPageProp = AccessTools.Property(typeof(RagFair), "OffersPerPageCount");
                    if (offersPerPageProp != null)
                    {
                        int currentValue = (int)offersPerPageProp.GetValue(_ragfair);

                        if (OriginalPageCount == -1)
                            OriginalPageCount = currentValue;

                        if (Activation.Value)
                            offersPerPageProp.SetValue(_ragfair, PageCapacity.Value);
                        else
                            offersPerPageProp.SetValue(_ragfair, OriginalPageCount);
                    }
                }
            }
        }

        // 按物品模板+子物品组合去重
        [HarmonyPatch(typeof(OfferViewList), "HandleNodeSelection")]
        class Patch_OfferViewList_HandleNodeSelection
        {
            static void Postfix(OfferViewList __instance)
            {
                if (!Activation.Value) return;

                // 取 OfferViewList 的 eviewListType_0 字段
                var viewTypeField = AccessTools.Field(typeof(OfferViewList), "_viewListType");
                if (viewTypeField == null) return;

                var viewType = (EViewListType)viewTypeField.GetValue(__instance);
                if (viewType != EViewListType.AllOffers && viewType != EViewListType.WishList) return;

                var _ragfairField = AccessTools.Field(typeof(OfferViewList), "_ragfair");
                var _ragfair = (RagFair)_ragfairField.GetValue(__instance);
                if (_ragfair == null) return;

                // ---- 新增：取 profileId 和 delaySinceOfferAdd ----
                string profileId = null;
                int delaySinceOfferAdd = 0;
                try
                {
                    // 从 OfferViewList 里反射拿 profileId（如果有对应字段）
                    var profileIdField = AccessTools.Field(typeof(OfferViewList), "_profileId");
                    if (profileIdField != null)
                        profileId = profileIdField.GetValue(__instance) as string;

                    // 从 GlobalConfiguration 拿 delaySinceOfferAdd
                    var gc = Singleton<GlobalConfiguration>.Instance;
                    if (gc != null && gc.RagFair != null)
                        delaySinceOfferAdd = gc.RagFair.delaySinceOfferAdd;
                }
                catch { /* 取不到就用默认值 */ }

                // 先获取全部订单，判断是否单项物品
                var allOffers = _ragfair.Offers?.ToList() ?? new List<Offer>();
                if (allOffers.Count == 0) return;

                // 统一判定可用性
                Func<Offer, bool> usable = o => IsOfferUsable(o, _ragfair, profileId, delaySinceOfferAdd);

                bool singleTemplate = allOffers.Select(o => o.Item.TemplateId).Distinct().Count() == 1;
                if (singleTemplate && SingleVanilla.Value) return;

                // 去除不可用订单
                List<Offer> offers;
                if (!singleTemplate || HideDis.Value)
                    offers = allOffers.Where(usable).ToList();
                else
                    offers = allOffers;

                // 去除GP订单
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
                    hasUsable = group.Any(usable);
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
                        .OrderByDescending(usable)
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
                        return new { Offer = o, Current = current, Max = max, Usable = usable(o) };
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

                        var candidates = group.Where(x => x.Current == maxCurrent).ToList();
                        var usableCandidates = candidates.Where(x => x.Usable).ToList();

                        var bestOffer = (usableCandidates.Count > 0 ? usableCandidates : candidates)
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
                            var rubCandidates = group
                                .Where(x => x.Current == maxCurrent &&
                                            x.Offer.Requirements != null &&
                                            x.Offer.Requirements.Any(r => r.TemplateId == rubId))
                                .ToList();
                            var usableRub = rubCandidates.Where(x => x.Usable).ToList();

                            var bestRubOffer = (usableRub.Count > 0 ? usableRub : rubCandidates)
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

                // 最后按价格排序
                if (RetainRub.Value || RetainFull.Value || singleTemplate)
                    filtered = filtered.OrderBy(o => o.SummaryCost).ToList();

                _ragfair.ClearOffers();
                foreach (var offer in filtered)
                    _ragfair.Offers.Add(offer);
            }
        }
    }
}
