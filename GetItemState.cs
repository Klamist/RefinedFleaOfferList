using System;
using System.Linq;
using EFT.InventoryLogic;

namespace RefinedFleaListPlugin
{
    public static class GetItemState
    {
        // 获取物品的当前状态与最大状态，返回 (isFull, current, max) 三元组。
        public static (float current, float max) Get(Item item)
        {
            // RepairableComponent (武器、护甲等)
            var reps = item.GetItemComponentsInChildren<RepairableComponent>(true).ToList();
            if (reps.Any())
            {
                float current = reps.Sum(r => r.Durability);
                float max = reps.Sum(r => r.MaxDurability);
                return (current, max);
            }

            // ArmorHolderComponent (插板内衬)
            ArmorHolderComponent armorHolder;
            if (item.TryGetItemComponent<ArmorHolderComponent>(out armorHolder) && armorHolder.MoveAbleArmorSlots.Any())
            {
                float current = armorHolder.MoveAbleArmorPlates.Count();
                float max = armorHolder.MoveAbleArmorSlots.Count();
                return (current, max);
            }

            // MedKitComponent
            MedKitComponent medKit;
            if (item.TryGetItemComponent<MedKitComponent>(out medKit))
            {
                return (medKit.HpResource, medKit.MaxHpResource);
            }

            // FoodDrinkComponent
            FoodDrinkComponent foodDrink;
            if (item.TryGetItemComponent<FoodDrinkComponent>(out foodDrink))
            {
                return (foodDrink.HpPercent, foodDrink.MaxResource);
            }

            // ResourceComponent (燃料桶)
            ResourceComponent resource;
            if (item.TryGetItemComponent<ResourceComponent>(out resource))
            {
                return (resource.Value, resource.MaxResource);
            }

            // SideEffectComponent
            SideEffectComponent sideEffect;
            if (item.TryGetItemComponent<SideEffectComponent>(out sideEffect))
            {
                return (sideEffect.Value, sideEffect.MaxResource);
            }

            // KeyComponent
            KeyComponent key;
            if (item.TryGetItemComponent<KeyComponent>(out key))
            {
                int remaining = key.Template.MaximumNumberOfUsage - key.NumberOfUsages;
                return (remaining, key.Template.MaximumNumberOfUsage);
            }

            // RepairKitsItemClass
            var kit = item as RepairKitsItemClass;
            if (kit != null)
            {
                return (kit.Resource, kit.MaxRepairResource);
            }

            // 默认情况
            return (0, 0);
        }
    }
}
