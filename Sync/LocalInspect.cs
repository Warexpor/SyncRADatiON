// Local books / EventScreen / lock flavor / Penrose cinematic — never party-replayed.
using UnityEngine;

namespace SyncRADation.Sync
{
    public static class LocalInspect
    {
        public static bool Dialogue(Dialogue d)
        {
            if (d == null) return true;
            if (InspectScreen()) return true;
            try
            {
                if (DialoguerFlavor((int)d._dialogue)) return true;
            }
            catch { }
            return UnderEventCamera(d.gameObject) || LockFlavor(d);
        }

        public static bool InspectScreen()
        {
            try { if (PlayerState.eventScreen) return true; } catch { }
            try
            {
                var gs = PlayerState.gameState;
                if (gs == PlayerState.gameStates.eventScreen || gs == PlayerState.gameStates.book)
                    return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// True when this object is in the local Elster's live room chunk.
        /// Other-room cutscenes / EventZones must not Invoke or StartCutscene —
        /// that yanks the observer through a traverse they never started.
        /// </summary>
        public static bool InLocalRoom(GameObject go)
        {
            if (go == null) return false;
            try
            {
                if (!go.activeInHierarchy) return false;
            }
            catch { return false; }
            try
            {
                var here = PlayerState.currentRoom;
                if (here == null) return true;
                Transform t = go.transform;
                Room room = null;
                while (t != null)
                {
                    if (t == here.transform) return true;
                    try
                    {
                        if (room == null)
                            room = t.GetComponent<Room>();
                    }
                    catch { }
                    t = t.parent;
                }
                return room == null;
            }
            catch { return true; }
        }

        public static bool DialoguerFlavor(int id)
        {
            switch (id)
            {
                case 0:
                case 6:
                case 17:
                case 20:
                case 21:
                case 22:
                case 23:
                case 24:
                case 25:
                case 26:
                case 27:
                    return true;
                default:
                    return false;
            }
        }

        static bool LockFlavor(Dialogue d)
        {
            if (d == null) return false;
            Transform t = d.gameObject != null ? d.gameObject.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<InteractiveLockSingle>() != null) return true;
                    if (t.GetComponent<InteractiveLock>() != null) return true;
                    if (t.GetComponent<ConnectedDoors>() != null) return true;
                    if (t.GetComponent<AutoTraverseDoor>() != null) return true;
                    if (t.GetComponent<UseItemInteraction>() != null) return true;
                    if (t.GetComponent<useItemPuzzleHint>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        static bool UnderEventCamera(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<EventScreen3DCam>() != null) return true;
                    if (t.GetComponent<EventScreenInteraction>() != null) return true;
                    if (t.GetComponent<EventOnlyRoom>() != null) return true;
                    if (t.GetComponent<EventScreen>() != null) return true;
                    if (t.GetComponent<ZoomInPoint>() != null) return true;
                    if (t.GetComponent<ItemPickup>() != null) return true;
                    if (t.GetComponent<ObservationDialogue>() != null) return true;
                    if (t.GetComponent<ObservationChoice>() != null) return true;
                    if (t.GetComponent<PEN_Airlock>() != null) return true;
                    if (t.GetComponent<PenroseAirlockNew>() != null) return true;
                    if (t.GetComponent<PenroseAirlock>() != null) return true;
                    if (t.GetComponent<PEN_Titles>() != null) return true;
                    if (t.GetComponent<AirlockInside>() != null) return true;
                    if (t.GetComponent<AirlockDoorLoadZone>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        public static bool AirlockCinematic(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<PEN_Titles>() != null) return true;
                    if (t.GetComponent<PEN_Airlock>() != null) return true;
                    if (t.GetComponent<PenroseAirlockNew>() != null) return true;
                    if (t.GetComponent<PenroseAirlock>() != null) return true;
                    if (t.GetComponent<AirlockInside>() != null) return true;
                    if (t.GetComponent<AirlockDoorLoadZone>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            return false;
        }

        public static bool Cinematic(GameObject go)
        {
            return AirlockCinematic(go) || UnderEventCamera(go);
        }

        public static bool LockWorld(GameObject go)
        {
            Transform t = go != null ? go.transform : null;
            while (t != null)
            {
                try
                {
                    if (t.GetComponent<InteractiveLockSingle>() != null) return true;
                    if (t.GetComponent<InteractiveLock>() != null) return true;
                    if (t.GetComponent<ConnectedDoors>() != null) return true;
                    if (t.GetComponent<AutoTraverseDoor>() != null) return true;
                    if (t.GetComponent<useItemPuzzleHint>() != null) return true;
                }
                catch { }
                t = t.parent;
            }
            try
            {
                if (go != null)
                {
                    var d = go.GetComponent<Dialogue>();
                    if (d != null && Dialogue(d)) return true;
                }
            }
            catch { }
            return false;
        }
    }
}
