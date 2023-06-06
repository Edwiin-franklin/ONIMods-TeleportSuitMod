﻿
using Epic.OnlineServices;
using HarmonyLib;
using PeterHan.PLib.AVC;
using PeterHan.PLib.Buildings;
using PeterHan.PLib.Core;
using PeterHan.PLib.Database;
using PeterHan.PLib.Detours;
using ProcGen.Map;
using System;
using static Grid;
using UnityEngine;
using static STRINGS.INPUT_BINDINGS;
using static UnityEngine.GraphicsBuffer;
using static STRINGS.BUILDINGS.PREFABS;
using System.Reflection;
using static STRINGS.MISC.NOTIFICATIONS;
using PeterHan.PLib.PatchManager;
using PeterHan.PLib.Options;
using System.Collections.Generic;
using UnityEngine.UI;
using static PathFinder;
using YamlDotNet.Core.Tokens;
using static OverlayLegend;
using static STRINGS.UI.USERMENUACTIONS;
using System.Linq;

namespace TeleportSuitMod
{
    public class TeleportSuitPatches : KMod.UserMod2
    {
        static FieldInfo SuitLockerFieldInfo = null;

        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            PUtil.InitLibrary();
            new POptions().RegisterOptions(this, typeof(TeleportSuitOptions));
            PBuildingManager buildingManager = new PBuildingManager();
            buildingManager.Register(TeleportSuitLockerConfig.CreateBuilding());
            new PLocalization().Register();
            new PVersionCheck().Register(this, new SteamVersionChecker());

            //生成字符串
            LocString.CreateLocStringKeys(typeof(TeleportSuitStrings.UI));

            GameObject gameObject = new GameObject(nameof(TeleportSuitWorldCountManager));
            gameObject.AddComponent<TeleportSuitWorldCountManager>();
            gameObject.SetActive(true);
        }

        [HarmonyPatch(typeof(SuitFabricatorConfig), "ConfigureRecipes")]
        public static class SuitFabricatorConfig_ConfigureRecipes_Patch
        {
            public static void Postfix()
            {
                ComplexRecipe.RecipeElement[] array15 = new ComplexRecipe.RecipeElement[2]
                {
                    new ComplexRecipe.RecipeElement(SimHashes.Tungsten.ToString(), 200f),
                    new ComplexRecipe.RecipeElement(SimHashes.Diamond.ToString(), 10f)
                };
                ComplexRecipe.RecipeElement[] array16 = new ComplexRecipe.RecipeElement[1]
                {
                    new ComplexRecipe.RecipeElement("Teleport_Suit".ToTag(), 1f, ComplexRecipe.RecipeElement.TemperatureOperation.Heated)
                };
                TeleportSuitConfig.recipe = new ComplexRecipe(ComplexRecipeManager.MakeRecipeID("SuitFabricator", array15, array16), array15, array16)
                {
                    time = TUNING.EQUIPMENT.SUITS.ATMOSUIT_FABTIME,
                    description = TeleportSuitStrings.EQUIPMENT.PREFABS.TELEPORT_SUIT.RECIPE_DESC,
                    nameDisplay = ComplexRecipe.RecipeNameDisplay.ResultWithIngredient,
                    fabricators = new List<Tag> { "SuitFabricator" },
                    requiredTech = DlcManager.IsContentActive("EXPANSION1_ID") ? TeleportSuitLockerConfig.techStringDlc : TeleportSuitLockerConfig.techStringVanilla,
                    sortOrder = 1
                };
                ComplexRecipe.RecipeElement[] array17 = new ComplexRecipe.RecipeElement[2]
                {
                    new ComplexRecipe.RecipeElement("Worn_Teleport_Suit".ToTag(), 1f),
                    new ComplexRecipe.RecipeElement(SimHashes.Diamond.ToString(), 5f)
                };
                ComplexRecipe.RecipeElement[] array18 = new ComplexRecipe.RecipeElement[1]
                {
                    new ComplexRecipe.RecipeElement("Teleport_Suit".ToTag(), 1f, ComplexRecipe.RecipeElement.TemperatureOperation.Heated)
                };
                TeleportSuitConfig.recipe = new ComplexRecipe(ComplexRecipeManager.MakeRecipeID("SuitFabricator", array17, array18), array17, array18)
                {
                    time = TUNING.EQUIPMENT.SUITS.ATMOSUIT_FABTIME,
                    description = TeleportSuitStrings.EQUIPMENT.PREFABS.TELEPORT_SUIT.RECIPE_DESC,
                    nameDisplay = ComplexRecipe.RecipeNameDisplay.ResultWithIngredient,
                    fabricators = new List<Tag> { "SuitFabricator" },
                    requiredTech = DlcManager.IsExpansion1Id("EXPANSION1_ID") ? TeleportSuitLockerConfig.techStringDlc : TeleportSuitLockerConfig.techStringVanilla,
                    sortOrder = 1
                };
            }
        }

        //修改后可以更新小人能拿到的东西
        [HarmonyPatch(typeof(MinionGroupProber), "IsReachable_AssumeLock")]
        public static class MinionGroupProber_IsReachable_AssumeLock_Patch
        {
            public static bool Prefix(int cell, ref bool __result)
            {
                if (TeleportSuitWorldCountManager.Instance.WorldCount.TryGetValue(
                    ClusterManager.Instance.GetWorld(Grid.WorldIdx[cell]).ParentWorldId, out int value)&&value>0)
                {
                    __result=TeleportSuitConfig.CanTeloportTo(cell);
                    return false;
                }
                return true;
            }
        }

        //修改穿着传送服的小人到各个格子的可达性,影响小人获取任务等等
        [HarmonyPatch(typeof(Navigator), nameof(Navigator.GetNavigationCost))]
        [HarmonyPatch(new Type[] { typeof(int) })]//GetNavigationCost函数有重载，需要确定参数类型
        public static class Navigator_GetNavigationCost_Patch
        {
            public static bool Prefix(Navigator __instance, int cell, ref int __result)
            {
                if ((__instance.flags&TeleportSuitConfig.TeleportSuitFlags)!=0)//穿着传送服
                {
                    __result=-1;
                    if (worldid.TryGetValue(__instance.PathProber, out int id)&&id!=-1)
                    {
                        if (Grid.IsValidCell(cell) && Grid.WorldIdx[cell] != byte.MaxValue
                                &&ClusterManager.Instance.GetWorld(Grid.WorldIdx[cell]).ParentWorldId==id
                                &&TeleportSuitConfig.CanTeloportTo(cell))
                        {
                            __result=1;
                        }
                    }
                    else
                    {
                        Console.WriteLine("error!!! in Navigator_GetNavigationCost_Patch");
                    }


                    return false;
                }

                return true;
            }
        }

        //当小人检测到下落时直接传送到安全可达地点，可以不加，加的话用传送快一些
        [HarmonyPatch(typeof(FallMonitor.Instance), nameof(FallMonitor.Instance.Recover))]
        public static class FallMonitor_Instance_Recover
        {
            public static bool Prefix(Navigator ___navigator, bool ___flipRecoverEmote)
            {
                if ((___navigator.flags&TeleportSuitConfig.TeleportSuitFlags)!=0)
                {
                    int cell = Grid.PosToCell(___navigator);
                    NavGrid.Transition[] transitions = ___navigator.NavGrid.transitions;
                    for (int i = 0; i < transitions.Length; i++)
                    {
                        NavGrid.Transition transition = transitions[i];
                        if (transition.isEscape && ___navigator.CurrentNavType == transition.start)
                        {
                            int num = transition.IsValid(cell, ___navigator.NavGrid.NavTable);
                            if (Grid.InvalidCell != num)
                            {
                                Vector2I vector2I = Grid.CellToXY(cell);
                                ___flipRecoverEmote = Grid.CellToXY(num).x < vector2I.x;
                                ___navigator.transform.SetPosition(Grid.CellToPosCBC(num, Grid.SceneLayer.Move));
                                ___navigator.CurrentNavType = transition.end;
                                FallMonitor.Instance sMI = ___navigator.GetSMI<FallMonitor.Instance>();
                                //sMI.UpdateFalling();
                                sMI.sm.isFalling.Set(false, sMI);
                                sMI.GoTo(sMI.sm.standing);
                                return false;
                            }
                        }
                    }

                }
                return true;
            }
        }

        public static Dictionary<PathProber, int> worldid = new Dictionary<PathProber, int>();
        //取消穿着传送服的小人到各个格子的可达性更新，并且记录小人的世界信息，
        //因为在Navigator_GetNavigationCost_Patch中获取世界可能会触发unity的gameobject获取报错
        [HarmonyPatch(typeof(PathProber), nameof(PathProber.UpdateProbe))]
        public static class PathProber_UpdateProbe_Patch
        {
            public static bool Prefix(PotentialPath.Flags flags, PathProber __instance, int cell)
            {
                if ((flags&TeleportSuitConfig.TeleportSuitFlags)!=0)
                {
                    if (Grid.IsValidCell(cell) && Grid.WorldIdx[cell] != byte.MaxValue)
                    {
                        worldid[__instance]= ClusterManager.Instance.GetWorld(Grid.WorldIdx[cell]).ParentWorldId;
                    }
                    else
                    {
                        worldid[__instance]=-1;
                    }

                    return false;
                }
                return true;
            }
        }

        //穿上传送服之后禁用寻路并传送小人
        [HarmonyPatch(typeof(Navigator), nameof(Navigator.AdvancePath))]
        public static class PathFinder_UpdatePath_Patch
        {
            public static void Prefix(Navigator __instance, ref NavTactic ___tactic, ref int ___reservedCell)
            {
                if (__instance.target!=null&&__instance.flags.HasFlag(TeleportSuitConfig.TeleportSuitFlags))
                {
                    int cellPreferences = ___tactic.GetCellPreferences(Grid.PosToCell(__instance.target), __instance.targetOffsets, __instance);
                    NavigationReservations.Instance.RemoveOccupancy(___reservedCell);
                    ___reservedCell =cellPreferences;
                    NavigationReservations.Instance.AddOccupancy(cellPreferences);
                    if (___reservedCell != NavigationReservations.InvalidReservation)
                    {
                        Equipment equipment = __instance.GetComponent<MinionIdentity>().GetEquipment();
                        if (equipment!=null)
                        {
                            Assignable assignable = equipment.GetAssignable(Db.Get().AssignableSlots.Suit);
                            if (assignable!=null)
                            {
                                TeleportSuitTank tank = assignable.GetComponent<TeleportSuitTank>();
                                if (tank!=null)
                                {
                                    tank.batteryCharge -=1f/TeleportSuitConfig.TELEPORTCOUNT;
                                }
                            }
                        }
                        __instance.Pause("teleporting");
                        Vector3 position = Grid.CellToPos(___reservedCell, CellAlignment.Bottom, (SceneLayer)25);
                        __instance.transform.SetPosition(position);
                        __instance.Unpause("teleported");
                        if (Grid.HasLadder[___reservedCell])
                        {
                            __instance.CurrentNavType = NavType.Ladder;
                        }
                        if (Grid.HasPole[___reservedCell])
                        {
                            __instance.CurrentNavType = NavType.Pole;

                        }
                        if (GameNavGrids.FloorValidator.IsWalkableCell(___reservedCell, Grid.CellBelow(___reservedCell), true))
                        {
                            __instance.CurrentNavType = NavType.Floor;
                        }
                    }
                }
            }
        }

        //取消存放柜复制人主动归还的任务
        [HarmonyPatch(typeof(SuitLocker.ReturnSuitWorkable), nameof(SuitLocker.ReturnSuitWorkable.CreateChore))]
        public static class SuitLocker_ReturnSuitWorkable_CreateChore_Patch
        {
            public static bool Prefix(SuitLocker.ReturnSuitWorkable __instance)
            {
                SuitLocker component = __instance.GetComponent<SuitLocker>();
                if (component.OutfitTags[0]==TeleportSuitGameTags.TeleportSuit)
                {
                    component.returnSuitWorkable.CancelChore();
                    TeleportSuitLocker teleportSuitLocker = __instance.gameObject.GetComponent<TeleportSuitLocker>();
                    if (teleportSuitLocker!=null)
                    {
                        teleportSuitLocker.unequipTeleportSuitWorkable.CreateChore();
                    }
                    return false;
                }
                return true;
            }
        }
        //取消选中穿着传送服的小人时绘制路径
        [HarmonyPatch(typeof(Navigator), nameof(Navigator.DrawPath))]
        public static class Navigator_DrawPath_Patch
        {
            public static bool Prefix(Navigator __instance)
            {
                if (__instance.gameObject.activeInHierarchy&&(__instance.flags&TeleportSuitConfig.TeleportSuitFlags)!=0)
                {
                    return false;
                }
                return true;
            }
        }

        //修改显示路径
        [HarmonyPatch(typeof(NavPathDrawer), "OnPostRender")]
        public static class NavPathDrawer_OnPostRender_Patch
        {
            static bool preDraw = false;
            public static bool Prefix(NavPathDrawer __instance)
            {
                Navigator nav = __instance.GetNavigator();
                if (nav!=null&&(nav.flags&TeleportSuitConfig.TeleportSuitFlags)!=0)
                {
                    if (OverlayScreen.Instance.mode != TeleportableOverlay.ID)
                    {
                        OverlayScreen.Instance.ToggleOverlay(TeleportableOverlay.ID);
                    }
                    preDraw=true;
                    return false;
                }
                if (preDraw)
                {
                    preDraw=false;
                    OverlayScreen.Instance.ToggleOverlay(OverlayModes.None.ID);
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(SuitLocker.ReturnSuitWorkable), nameof(SuitLocker.ReturnSuitWorkable.CancelChore))]

        public static class SuitLocker_ReturnSuitWorkable_CancelChore_Patch
        {
            public static bool Prefix(SuitLocker.ReturnSuitWorkable __instance)
            {
                SuitLocker component = __instance.GetComponent<SuitLocker>();

                if (component.OutfitTags[0].Name==TeleportSuitGameTags.TeleportSuit.Name)
                {
                    TeleportSuitLocker teleportSuitLocker = component.gameObject.GetComponent<TeleportSuitLocker>();
                    if (teleportSuitLocker!=null)
                    {
                        teleportSuitLocker.unequipTeleportSuitWorkable.CancelChore();
                    }
                }
                return true;
            }
        }


        [HarmonyPatch(typeof(SuitLocker), nameof(SuitLocker.UpdateSuitMarkerStates))]
        public static class SuitLocker_UpdateSuitMarkerStates_Patch
        {
            public static bool Prefix(GameObject self)
            {
                if (self==null) return false;
                SuitLocker component = self.GetComponent<SuitLocker>();
                if (component != null&&component.OutfitTags[0].Name==TeleportSuitGameTags.TeleportSuit.Name)
                {
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(OverlayMenu), "InitializeToggles")]
        public static class OverlayMenu_InitializeToggles_Patch
        {
            public static void Postfix(List<OverlayMenu.ToggleInfo> ___overlayToggleInfos)
            {
                Type type = typeof(OverlayMenu).GetNestedType("OverlayToggleInfo", BindingFlags.NonPublic|BindingFlags.Instance);
                object[] parameters = new object[] {
                    TeleportSuitStrings.UI.OVERLAYS.TELEPORTABLE.BUTTON.ToString(),
                    "overlay_suit",
                    TeleportableOverlay.ID,
                    DlcManager.IsContentActive("EXPANSION1_ID") ? TeleportSuitLockerConfig.techStringDlc : TeleportSuitLockerConfig.techStringVanilla,
                    Action.NumActions,
                    TeleportSuitStrings.UI.TOOLTIPS.TELEPORTABLEOVERLAYSTRING.ToString(),
                    TeleportSuitStrings.UI.OVERLAYS.TELEPORTABLE.BUTTON.ToString()
                };
                object obj = Activator.CreateInstance(type, parameters);
                ___overlayToggleInfos.Add((KIconToggleMenu.ToggleInfo)obj);
            }
        }

        [HarmonyPatch(typeof(SimDebugView), "OnPrefabInit")]
        public static class SimDebugView_OnPrefabInit_Patch
        {
            public static void Postfix(Dictionary<HashedString, Func<SimDebugView, int, Color>> ___getColourFuncs)
            {
                ___getColourFuncs.Add(TeleportableOverlay.ID, TeleportableOverlay.GetOxygenMapColour);
            }
        }

        [HarmonyPatch(typeof(OverlayScreen), "RegisterModes")]
        public static class OverlayScreen_RegisterModes_Patch
        {
            public static void Postfix(OverlayScreen __instance)
            {
                typeof(OverlayScreen).GetMethod("RegisterMode", BindingFlags.NonPublic|BindingFlags.Instance).Invoke(__instance, new object[] { new TeleportableOverlay() });
            }
        }
        [HarmonyPatch(typeof(StatusItem), "GetStatusItemOverlayBySimViewMode")]
        public static class StatusItem_GetStatusItemOverlayBySimViewMode_Patch
        {
            public static void Prefix(Dictionary<HashedString, StatusItem.StatusItemOverlays> ___overlayBitfieldMap)
            {
                if (!___overlayBitfieldMap.ContainsKey(TeleportableOverlay.ID))
                {
                    ___overlayBitfieldMap.Add(TeleportableOverlay.ID, StatusItem.StatusItemOverlays.None);
                }
            }
        }

        [HarmonyPatch(typeof(OverlayLegend), "OnSpawn")]
        public static class OverlayLegend_OnSpawn_Patch
        {
            public static void Prefix(List<OverlayLegend.OverlayInfo> ___overlayInfoList)
            {
                OverlayLegend.OverlayInfo info = new OverlayLegend.OverlayInfo();
                info.name = "STRINGS.UI.OVERLAYS.TELEPORTABLE.NAME";
                info.mode=TeleportableOverlay.ID;
                info.infoUnits=new List<OverlayInfoUnit>();
                info.isProgrammaticallyPopulated=true;
                ___overlayInfoList.Add(info);
            }
        }
    }
}
