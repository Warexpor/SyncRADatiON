// Host-authoritative world ItemPickup: claim → grant claimer, hide for all peers; a partial take gives the rest back.
using System.Collections.Generic;
using SyncRADation.ItemSystem;
using SyncRADation.Sync;
using UnityEngine;

namespace SyncRADation.Networking
{
    public sealed class WorldPickupSyncService
    {
        // ------------------------------------------------------------------ host send state

        private float _timer;
        private const float Interval = 0.4f;
        private bool _needFull = true;

        private struct Sent
        {
            public bool Triggered;
            public bool Active;
        }
        // What every peer was last told per WorldId. Never written inside a unicast dump (one peer only).
        private readonly Dictionary<ulong, Sent> _sent = new Dictionary<ulong, Sent>();
        private readonly List<WorldPickupEntry> _tickList = new List<WorldPickupEntry>(32);

        // ------------------------------------------------------------------ claims

        private const int NoClaimer = -1;

        /// <summary>Who holds a WorldId. Claimer = NoClaimer: the world marked it (native triggered / prop gone) or a client mirror.</summary>
        private struct Claim
        {
            public int Claimer;
            public Items.itemlist Item;
        }
        private readonly Dictionary<ulong, Claim> _claims = new Dictionary<ulong, Claim>();
        /// <summary>Claimed unique Key/Object enums: a second instance of the same unique hides too. Kept across scene refresh.</summary>
        private readonly HashSet<ushort> _claimedItems = new HashSet<ushort>();
        /// <summary>Once-per-WorldId party onPickup Invoke (Dig H). Cleared on scene refresh / claim release.</summary>
        private readonly HashSet<ulong> _partyOnPickupFired = new HashSet<ulong>();
        /// <summary>Host: count a partial take left on a prop this visit (the join dump carries it).</summary>
        private readonly Dictionary<ulong, int> _remainder = new Dictionary<ulong, int>();
        // Host: props DynamicSupply.Entered switched off for this run (sent as Count = -1, see WithheldCount).
        private readonly HashSet<ulong> _withheld = new HashSet<ulong>();

        /// <summary>WorldPickupEntry.Count of an untriggered prop the host's DynamicSupply withheld (switched off).</summary>
        public const int WithheldCount = -1;

        // ------------------------------------------------------------------ scene index

        private readonly Dictionary<ulong, ItemPickup> _byId = new Dictionary<ulong, ItemPickup>();
        // Scan-time list of Key/Object props: the only ones IsClaimedPickup can hide by item enum
        // (a second instance of a claimed unique). HideClaimed(null) walks _claims + this, not every prop.
        private struct UniqueProp
        {
            public ulong Id;
            public ItemPickup P;
            public ushort Item;
        }
        private readonly List<UniqueProp> _uniqueProps = new List<UniqueProp>(8);
        private bool _scanned;
        // A miss in _byId (prop instantiated after the scan) rescans at most this often.
        private float _lastRescanAt = -10f;
        private const float RescanCooldown = 1f;

        // ------------------------------------------------------------------ local native takes in flight

        /// <summary>
        /// One local native take of a prop (this peer ran ItemPickup.pickUp), from pickUp to the end of its claim.
        /// Open: yes/no (or the pickup line) open. Answered: "yes", native release ~0.1 s away. AwaitVerdict: native
        /// already added, the host's grant/deny is still out. Reverting: the claim lost, the native add is taken back
        /// as soon as it shows. Every phase has one Deadline; <see cref="Expire"/> is the only timeout path.
        /// </summary>
        private enum Phase : byte { Open, Answered, AwaitVerdict, Reverting }

        private sealed class Take
        {
            public ItemPickup Prop;
            public Phase Phase;
            public float Deadline;
            /// <summary>Native release ran (or can no longer run): a hide no longer cancels its Invoke("release").</summary>
            public bool ReleaseRan;
            /// <summary>A hide arrived while release was still to come (SetActive(false) would cancel the Invoke).</summary>
            public bool HideHeld;
            /// <summary>Claimer: native release does the bag add, so a grant must not AddItem and a deny must undo it.</summary>
            public bool NativeAdds;
            /// <summary>Compare the bag gain at release with Count (partial AddItemToMax).</summary>
            public bool MeasureGain;
            public float Since;
            public Items.itemlist Item;
            public int Count;
            public int BagBefore;
            /// <summary>Weapon magazine before native release (-1 = not ammo for a held weapon): MagFill gains.</summary>
            public int MagBefore;
        }

        private readonly Dictionary<ulong, Take> _takes = new Dictionary<ulong, Take>();
        private readonly List<ulong> _takeScratch = new List<ulong>(4);

        // Native ItemPickup.dialoguerCallback schedules Invoke("release", 0.1) (Ghidra ItemPickup.c). Unity cancels a
        // pending Invoke once the GameObject is deactivated, so a prop is not hidden for its own taker until release ran.
        private const float ReleaseWaitOpen = 120f;
        private const float ReleaseWaitCallback = 3f;
        private const float VerdictWait = 30f;
        private const float RevertWindow = 4f;

        // ================================================================== take lifecycle

        static ulong IdOf(ItemPickup p)
        {
            if (p == null) return 0;
            try { return p.gameObject != null ? WorldId.FromGameObject(p.gameObject) : 0UL; }
            catch (System.Exception e) { Guard.Swallow(e); return 0; }
        }

        Take TakeOf(ulong id, ItemPickup p)
        {
            Take t;
            if (!_takes.TryGetValue(id, out t))
            {
                t = new Take { Phase = Phase.Open, MagBefore = -1 };
                _takes[id] = t;
            }
            if (p != null) t.Prop = p;
            return t;
        }

        /// <summary>
        /// Native pickUp / dialoguerCallback is running for this prop on the local peer: its release is still to come,
        /// so HidePickup holds back. callbackSeen = the yes/no was answered (release ~0.1 s away).
        /// </summary>
        public void MarkReleasePending(ItemPickup p, bool callbackSeen)
        {
            ulong id = IdOf(p);
            if (id == 0) return;
            var t = TakeOf(id, p);
            t.ReleaseRan = false;
            if (t.Phase == Phase.Reverting || t.Phase == Phase.AwaitVerdict) return;
            t.Phase = callbackSeen ? Phase.Answered : Phase.Open;
            t.Deadline = Time.unscaledTime + (callbackSeen ? ReleaseWaitCallback : ReleaseWaitOpen);
        }

        /// <summary>
        /// The take was confirmed: native release (~0.1 s away) adds the item. Snapshot the bag now so the gain can be
        /// measured (partial take) and, for a client claim (awaitVerdict), so a deny can take back what native added.
        /// </summary>
        public void ExpectNativeAdd(ulong id, ItemPickup p, Items.itemlist item, int count, bool awaitVerdict)
        {
            if (id == 0 || item == Items.itemlist.None) return;
            var t = TakeOf(id, p);
            t.Item = item;
            t.Count = count > 0 ? count : 1;
            t.BagBefore = ItemBag.CountInBag(item);
            t.MagBefore = MagAmmoOf(item);
            t.Since = Time.unscaledTime;
            t.MeasureGain = true;
            t.NativeAdds = awaitVerdict;
            if (t.Phase == Phase.Open) t.Phase = Phase.Answered;
        }

        /// <summary>pickUp produced no dialogue (refused / threw): no release will follow.</summary>
        public void ClearReleasePending(ItemPickup p)
        {
            ulong id = IdOf(p);
            Take t;
            if (id == 0 || !_takes.TryGetValue(id, out t) || t.ReleaseRan) return;
            ReleaseDone(id, t);
        }

        /// <summary>Native release finished: measure the gain (a partial take gives the rest back), then the held hide.</summary>
        public void OnNativeRelease(ItemPickup p)
        {
            ulong id = IdOf(p);
            Take t;
            if (id == 0 || !_takes.TryGetValue(id, out t)) return;
            if (t.MeasureGain)
            {
                t.MeasureGain = false;
                SettleGain(id, t, p);
            }
            ReleaseDone(id, t);
        }

        /// <summary>Release ran (or will never run): apply a held hide and move to whatever the take still waits for.</summary>
        void ReleaseDone(ulong id, Take t)
        {
            t.ReleaseRan = true;
            if (t.HideHeld)
            {
                t.HideHeld = false;
                if (t.Prop != null) HideOnePickup(t.Prop);
            }
            if (t.Phase == Phase.Reverting)
            {
                if (TryRevert(t)) _takes.Remove(id);
                return;
            }
            if (t.NativeAdds)
            {
                t.Phase = Phase.AwaitVerdict;
                t.Deadline = t.Since + VerdictWait;
                return;
            }
            _takes.Remove(id);
        }

        bool IsReleasePending(ulong id)
        {
            Take t;
            if (!_takes.TryGetValue(id, out t) || t.ReleaseRan) return false;
            if (Time.unscaledTime <= t.Deadline || ReleaseStillComing()) return true;
            Expire(id, t);
            return _takes.TryGetValue(id, out t) && !t.ReleaseRan;
        }

        /// <summary>The release Invoke runs on scaled time and the pickup state stays "dialogue" until it ran.</summary>
        static bool ReleaseStillComing()
        {
            try { return Time.timeScale <= 0.0001f || PlayerState.gameState == PlayerState.gameStates.dialogue; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>True when native pickUp opened its dialogue (state == dialogue), i.e. a release is coming.</summary>
        public static bool DialogueOpenNow()
        {
            try { return PlayerState.gameState == PlayerState.gameStates.dialogue; }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>The single timeout path: a take's phase deadline passed.</summary>
        void Expire(ulong id, Take t)
        {
            if (!t.ReleaseRan)
            {
                // Paused (timeScale 0) or the pickup dialogue still open: Invoke("release") has not had a chance to
                // run, so the wait does not run out and a hide never lands under an open dialogue.
                if (ReleaseStillComing())
                {
                    t.Deadline = Time.unscaledTime + ReleaseWaitCallback;
                    return;
                }
                // Release never ran (prop gone / dialogue aborted): nothing left to measure; the take moves on to
                // whatever it still waits for (verdict / revert), each with its own deadline.
                t.MeasureGain = false;
                ReleaseDone(id, t);
                return;
            }
            // AwaitVerdict ran out (grant / deny never came) or Reverting gave up.
            _takes.Remove(id);
        }

        /// <summary>Per frame (both roles): reverts poll the bag, every take's deadline is checked.</summary>
        void TickTakes()
        {
            if (_takes.Count == 0) return;
            float now = Time.unscaledTime;
            _takeScratch.Clear();
            foreach (var kvp in _takes)
                _takeScratch.Add(kvp.Key);
            for (int i = 0; i < _takeScratch.Count; i++)
            {
                ulong id = _takeScratch[i];
                Take t;
                if (!_takes.TryGetValue(id, out t)) continue;
                if (t.Phase == Phase.Reverting && TryRevert(t))
                {
                    // The add showed up, so release has run: a held hide can land now.
                    if (t.HideHeld && t.Prop != null) HideOnePickup(t.Prop);
                    _takes.Remove(id);
                    continue;
                }
                if (now > t.Deadline) Expire(id, t);
            }
        }

        // ================================================================== gain / revert

        /// <summary>
        /// Magazine round count of the weapon that fires this ammo, or -1 when it is not ammo / no weapon is held.
        /// Native release takes the MagFill branch when the bag is full: the ammo goes straight into the
        /// magazine and never touches the bag, so a bag-count delta alone reads it as "gained nothing".
        /// </summary>
        static int MagAmmoOf(Items.itemlist kind)
        {
            try
            {
                var an = InventoryManager.getItem(kind);
                if (an == null || an.type != AnItem.AnItemType.Ammo) return -1;
                var w = InventoryManager.getWeaponFromAmmo(an);
                if (w == null || w.parentItem == null || !InventoryManager.hasItem(w.parentItem)) return -1;
                return w.magAmmo;
            }
            catch (System.Exception e) { Guard.Swallow(e); return -1; }
        }

        /// <summary>True when a full bag would still take this ammo into the weapon magazine (native MagFill).</summary>
        public static bool WouldMagFill(Items.itemlist kind)
        {
            try
            {
                var an = InventoryManager.getItem(kind);
                if (an == null || an.type != AnItem.AnItemType.Ammo) return false;
                var settings = PlayerState.settings;
                if (settings == null || !settings.allowMagfilling) return false;
                var w = InventoryManager.getWeaponFromAmmo(an);
                if (w == null || w.parentItem == null || !InventoryManager.hasItem(w.parentItem)) return false;
                return w.magAmmo < w.MagSize;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        /// <summary>Units native release really gave this peer since the snapshot (bag delta + MagFill).</summary>
        static int Gained(Take t, out int magGain)
        {
            int gained = ItemBag.CountInBag(t.Item) - t.BagBefore;
            if (gained < 0) gained = 0;
            magGain = 0;
            if (t.MagBefore >= 0)
            {
                int mag = MagAmmoOf(t.Item) - t.MagBefore;
                if (mag > 0) magGain = mag;
            }
            return gained;
        }

        /// <summary>
        /// Native release kept the part the bag could not hold on the prop (count = remainder + SProgress SetInt, no
        /// Destroy; Ghidra ItemPickup.c release). The prop is the remainder's only home: release the claim so every
        /// peer's prop shows it and anyone can take it.
        /// </summary>
        void SettleGain(ulong id, Take t, ItemPickup p)
        {
            int magGain;
            int bagGain = Gained(t, out magGain);
            // hasItem/getCount are ring-masqueraded outside release, so only the raw bag delta is logged.
            PlaytestLog.Event("Pickup", "native release id=" + id.ToString("X16") + " " + t.Item
                + " gained=" + bagGain + "/" + t.Count);
            int remainder = t.Count - bagGain - magGain;
            if (remainder <= 0 || t.Item == Items.itemlist.None) return;
            // Key/Object ride the party key ring; they never stack or overflow.
            if (PartyKeyRing.IsKeyOrObject(t.Item)) return;
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;

            int onProp = -1;
            try { if (p != null && p.gameObject != null) onProp = p.count; } catch (System.Exception e) { Guard.Swallow(e); }
            if (onProp != remainder)
            {
                // Measurement and native disagree: never give back more than native kept. The claim stays for this
                // visit; the prop comes back with its saved count (LoadState) on the next scene load.
                PlaytestLog.Warn("Pickup", "partial " + t.Item + " measured x" + remainder + " prop x" + onProp
                    + " id=" + id.ToString("X16") + " — claim kept");
                return;
            }
            PlaytestLog.Event("Pickup", "partial " + t.Item + " x" + remainder + " stays on prop id=" + id.ToString("X16"));
            if (NetGate.HostRole)
                HostReleaseRemainder(id, net.LocalPlayerId, remainder);
            else
                net.WorldPickupHandlers.SendWorldPickupClaim(id, t.Item, t.Count, remaining: remainder);
        }

        /// <summary>
        /// Take back a native bag add after the claim lost (host deny, or claimed while the yes/no dialogue was open).
        /// Safe before native release has run: the take polls the bag until the add shows or RevertWindow ends.
        /// </summary>
        void StartRevert(ulong id, Take t)
        {
            t.Phase = Phase.Reverting;
            t.NativeAdds = false;
            t.MeasureGain = false;
            t.Deadline = Time.unscaledTime + RevertWindow;
            if (TryRevert(t)) _takes.Remove(id);
        }

        /// <summary>Claim lost while this peer's yes/no was open (native release still adds): undo it.</summary>
        public void RevertNativeGrantNow(ulong id, ItemPickup p, Items.itemlist item, int count)
        {
            if (id == 0 || item == Items.itemlist.None) return;
            var t = TakeOf(id, p);
            t.Item = item;
            t.Count = count > 0 ? count : 1;
            t.BagBefore = ItemBag.CountInBag(item);
            t.MagBefore = MagAmmoOf(item);
            StartRevert(id, t);
        }

        bool TryRevert(Take t)
        {
            int magGain;
            int gained = Gained(t, out magGain);
            if (gained <= 0 && magGain <= 0) return false;
            int take = gained < t.Count ? gained : t.Count;
            NetGate.BeginApply();
            try
            {
                var an = InventoryManager.getItem(t.Item);
                if (an != null)
                {
                    // Bag entries may be a different AnItem instance than the catalog one: remove the one the bag holds.
                    if (take > 0) InventoryManager.RemoveItem(PartyKeyRing.FindInBag(an) ?? an, take);
                    if (magGain > 0)
                    {
                        var w = InventoryManager.getWeaponFromAmmo(an);
                        if (w != null)
                        {
                            int undo = magGain < t.Count ? magGain : t.Count;
                            w.magAmmo = System.Math.Max(t.MagBefore, w.magAmmo - undo);
                            take += undo;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] revert native grant: " + ex.Message);
            }
            finally { NetGate.EndApply(); }
            PlaytestLog.Event("Pickup", "reverted native grant " + t.Item + " x" + take + " (claimed by another player)");
            NotifyGone(t.Item, t.Prop);
            return true;
        }

        static void NotifyGone(Items.itemlist item, ItemPickup prop)
        {
            // Native "cannot carry" line is the closest existing message; the item is gone for this peer.
            if (prop == null) return;
            try
            {
                if (prop.gameObject == null) return;
                if (PlayerState.gameState != PlayerState.gameStates.play) return;
                var an = InventoryManager.getItem(item);
                if (an != null) PartyKeyRing.BindUseDialogue(an);
                NetGate.BeginApply();
                try { Dialoguer.StartDialogue((int)prop._cannotDialogue); }
                finally { NetGate.EndApply(); }
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] gone message: " + ex.Message);
            }
        }

        /// <summary>Client: host refused the claim — someone else owns the prop; undo the native add.</summary>
        public void ApplyDeny(WorldPickupDenyMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || msg.TargetPlayerId != net.LocalPlayerId) return;
            ulong id = unchecked((ulong)msg.WorldId);
            Take t;
            if (!_takes.TryGetValue(id, out t) || !t.NativeAdds)
            {
                if (t != null) t.MeasureGain = false;
                PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " (no native add to undo)");
                return;
            }
            PlaytestLog.Event("Pickup", "deny id=" + id.ToString("X16") + " undo native " + t.Item);
            EnsureScanned();
            ItemPickup p;
            if (_byId.TryGetValue(id, out p) && Alive(p)) t.Prop = p;
            StartRevert(id, t);
        }

        // ================================================================== session / scene

        public void RefreshScene()
        {
            _scanned = false;
            _needFull = true;
            _sent.Clear();
            _claims.Clear();
            _byId.Clear();
            _uniqueProps.Clear();
            _remainder.Clear();
            _withheld.Clear();
            _lastRescanAt = -10f;
            _partyOnPickupFired.Clear();
            // _claimedItems survives: a unique claimed in another scene stays claimed.
            // Props of the old scene are gone: their held hides / release waits / gain checks die with them. A claim
            // still waiting for its verdict (or reverting) keeps its expiry so a grant/deny landing just after the
            // load still balances the bag.
            _takeScratch.Clear();
            foreach (var kvp in _takes)
                _takeScratch.Add(kvp.Key);
            for (int i = 0; i < _takeScratch.Count; i++)
            {
                var t = _takes[_takeScratch[i]];
                t.Prop = null;
                t.HideHeld = false;
                t.MeasureGain = false;
                if (!t.ReleaseRan) ReleaseDone(_takeScratch[i], t);
            }
            _timer = 0f;
        }

        public void Reset()
        {
            _scanned = false;
            _needFull = true;
            _sent.Clear();
            _claims.Clear();
            _claimedItems.Clear();
            _byId.Clear();
            _uniqueProps.Clear();
            _remainder.Clear();
            _withheld.Clear();
            _lastRescanAt = -10f;
            _partyOnPickupFired.Clear();
            _takes.Clear();
            _timer = 0f;
        }

        public void RequestFullSend() => _needFull = true;

        // ================================================================== items / claims

        /// <summary>
        /// The item a pickup holds (_item, else _itemEnum). bindCatalog fills a missing _item from the catalog
        /// (native pickUp reads _item only); template scans pass false so prefabs are never touched.
        /// </summary>
        public static Items.itemlist ResolveItem(ItemPickup p, bool bindCatalog = true)
        {
            if (p == null) return Items.itemlist.None;
            try
            {
                if (p._item != null && p._item._item != Items.itemlist.None)
                    return p._item._item;
                var kind = p._itemEnum;
                if (kind != Items.itemlist.None && bindCatalog && p._item == null)
                    p._item = InventoryManager.getItem(kind);
                return kind;
            }
            catch (System.Exception e) { Guard.Swallow(e); return Items.itemlist.None; }
        }

        static bool IsLocalHost(int playerId)
        {
            return NetGate.HostRole && playerId == LanNetworkManager.Instance.LocalPlayerId;
        }

        static bool Alive(ItemPickup p)
        {
            if (p == null) return false;
            try { return p.gameObject != null; }
            catch { return false; }
        }

        public bool IsClaimed(ulong worldId) => worldId != 0 && _claims.ContainsKey(worldId);

        /// <summary>Host: DynamicSupply switched this prop off; every peer gets it off (next state tick, join dump).</summary>
        public void NoteWithheld(ItemPickup p)
        {
            if (p == null || !NetGate.Host) return;
            ulong id;
            try { id = WorldId.FromGameObject(p.gameObject); }
            catch (System.Exception e) { Guard.Swallow(e); return; }
            if (id != 0 && _withheld.Add(id))
                PlaytestLog.Event("Pickup", "dynamic supply withheld id=" + id.ToString("X16"));
        }

        /// <summary>Record a claim this peer learned about (no claimer known) without overwriting a host record.</summary>
        void MarkClaimed(ulong id, Items.itemlist item)
        {
            Claim c;
            bool had = _claims.TryGetValue(id, out c);
            bool unique = PartyKeyRing.IsKeyOrObject(item);
            if (!had || (c.Item == Items.itemlist.None && unique))
                _claims[id] = new Claim { Claimer = had ? c.Claimer : NoClaimer, Item = unique ? item : c.Item };
            if (unique) _claimedItems.Add((ushort)item);
        }

        /// <summary>Drop a claim: the WorldId is takeable again; a unique's enum mark goes when no other claim holds it.</summary>
        void ForgetClaim(ulong id)
        {
            Claim c;
            if (!_claims.TryGetValue(id, out c)) return;
            _claims.Remove(id);
            _partyOnPickupFired.Remove(id);
            Take t;
            if (_takes.TryGetValue(id, out t)) t.HideHeld = false;
            if (c.Item == Items.itemlist.None) return;
            foreach (var kvp in _claims)
                if (kvp.Value.Item == c.Item) return;
            _claimedItems.Remove((ushort)c.Item);
        }

        /// <summary>
        /// NG+ Artifact softlock guard: KeyOfSacrifice lives under NGP_only in the
        /// bookstore. Death tarot MeatBlocker ID "Death" seals that wing (wiki).
        /// True while a live unclaimed KeyOfSacrifice world pickup still exists.
        /// </summary>
        public bool KeyOfSacrificeAvailableUnclaimed()
        {
            if (PartyKeyRing.Has(Items.itemlist.KeyOfSacrifice)) return false;
            if (_claimedItems.Contains((ushort)Items.itemlist.KeyOfSacrifice)) return false;
            EnsureScanned();
            foreach (var kvp in _byId)
            {
                var p = kvp.Value;
                if (p == null) continue;
                try
                {
                    if (ResolveItem(p) == Items.itemlist.KeyOfSacrifice && p.gameObject != null && p.gameObject.activeInHierarchy)
                        return true;
                }
                catch (System.Exception e) { Guard.Swallow(e); }
            }
            return false;
        }

        public bool HoldTarotDeathForSacrifice(Items.itemlist item)
        {
            return item == Items.itemlist.TarotDeath && KeyOfSacrificeAvailableUnclaimed();
        }

        public bool IsClaimedPickup(ItemPickup p)
        {
            if (p == null || DroppedItemRegistry.IsDropped(p)) return false;
            ulong id = IdOf(p);
            if (id != 0 && _claims.ContainsKey(id)) return true;
            try { return p._item != null && _claimedItems.Contains((ushort)p._item._item) && UniqueWorldItem(p); }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        static bool UniqueWorldItem(ItemPickup p)
        {
            try
            {
                if (p == null || p._item == null) return false;
                var t = p._item.type;
                return t == AnItem.AnItemType.Key || t == AnItem.AnItemType.Object;
            }
            catch (System.Exception e) { Guard.Swallow(e); return false; }
        }

        void NoteClaimedItem(Items.itemlist item)
        {
            if (item == Items.itemlist.None || !PartyKeyRing.IsKeyOrObject(item)) return;
            _claimedItems.Add((ushort)item);
        }

        static Items.itemlist ItemOfProp(ItemPickup p)
        {
            try { return p != null && p._item != null ? p._item._item : Items.itemlist.None; }
            catch (System.Exception e) { Guard.Swallow(e); return Items.itemlist.None; }
        }

        // ================================================================== hide / restore

        public void NotifyRevealed() => HideClaimed(null);

        public void HidePickup(ItemPickup p)
        {
            if (p == null) return;
            if (_takes.Count > 0)
            {
                ulong id = IdOf(p);
                if (id != 0 && IsReleasePending(id))
                {
                    // Hiding now would SetActive(false) under the pending Invoke("release").
                    _takes[id].HideHeld = true;
                    PlaytestLog.Verbose("Pickup", "hide deferred until native release id=" + id.ToString("X16"));
                    return;
                }
            }
            HideOnePickup(p);
        }

        static void HideOnePickup(ItemPickup p)
        {
            if (!Alive(p)) return;
            try
            {
                p.triggered = true;
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = true;
                    it.enabled = false;
                }
                if (!p.dontDestroyOnPickup)
                    p.gameObject.SetActive(false);
                else
                    p.enabled = false;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>Undo HideOnePickup after the host gave a claim back (declined yes/no, partial take, Triggered=false broadcast).</summary>
        static void RestorePickup(ItemPickup p)
        {
            if (!Alive(p)) return;
            try
            {
                p.triggered = false;
                p.enabled = true;
                var it = p.GetComponent<Interaction>();
                if (it != null)
                {
                    it.triggered = false;
                    it.enabled = true;
                }
                p.gameObject.SetActive(true);
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>A partial take's remainder: native count, and no DynamicDifficulty re-roll on the next look.</summary>
        static void SetPropCount(ItemPickup p, int count)
        {
            if (!Alive(p) || count <= 0) return;
            try
            {
                p.count = count;
                p.firstObserved = true;
            }
            catch (System.Exception e) { Guard.Swallow(e); }
        }

        /// <summary>HideOnePickup already ran and nothing re-enabled the prop itself (parent wakes keep activeSelf false).</summary>
        static bool AlreadyHidden(ItemPickup p)
        {
            try
            {
                if (p.gameObject == null) return true;
                return p.dontDestroyOnPickup ? !p.enabled : !p.gameObject.activeSelf;
            }
            catch { return true; }
        }

        /// <summary>
        /// Re-hide claimed props after a parent chunk/content wakes (SetActive re-enables children).
        /// </summary>
        public void HideClaimed(GameObject root)
        {
            EnsureScanned();
            if (root != null)
            {
                ItemPickup[] picks = null;
                try { picks = root.GetComponentsInChildren<ItemPickup>(true); } catch (System.Exception e) { Guard.Swallow(e); }
                if (picks == null) return;
                for (int i = 0; i < picks.Length; i++)
                {
                    var p = picks[i];
                    if (p == null || DroppedItemRegistry.IsDropped(p)) continue;
                    ulong id = IdOf(p);
                    // Dig H: chunk wake HideClaimed used to Hide only — never EnsurePartyOnPickup. takeSpear /
                    // StartOutro / MeatBlocker onPickup stayed silent if claim/state hide ran before scan.
                    if (!(id != 0 && _claims.ContainsKey(id)) && !IsClaimedPickup(p)) continue;
                    if (id != 0)
                    {
                        MarkClaimed(id, Items.itemlist.None);
                        EnsurePartyOnPickup(id, p);
                    }
                    HidePickup(p);
                }
                return;
            }

            // Runs on every state message / door event: only claimed ids and the scan's unique props, ids cached.
            foreach (var kvp in _claims)
            {
                ItemPickup p;
                if (!_byId.TryGetValue(kvp.Key, out p) || p == null) continue;
                if (!AlreadyHidden(p)) HidePickup(p);
            }
            if (_claimedItems.Count == 0) return;
            for (int i = 0; i < _uniqueProps.Count; i++)
            {
                var u = _uniqueProps[i];
                if (u.P == null || _claims.ContainsKey(u.Id) || !_claimedItems.Contains(u.Item)) continue;
                if (!AlreadyHidden(u.P)) HidePickup(u.P);
            }
        }

        private void EnsureScanned()
        {
            if (_scanned) return;
            _byId.Clear();
            _uniqueProps.Clear();
            _lastRescanAt = Time.unscaledTime;
            var all = WorldLookup.All<ItemPickup>();
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var p = all[i];
                    if (p == null) continue;
                    try
                    {
                        if (p.slave || DroppedItemRegistry.IsDropped(p)) continue;
                        ulong id = WorldId.FromGameObject(p.gameObject);
                        if (id == 0 || _byId.ContainsKey(id)) continue;
                        _byId[id] = p;
                        if (UniqueWorldItem(p))
                            _uniqueProps.Add(new UniqueProp { Id = id, P = p, Item = (ushort)p._item._item });
                    }
                    catch (System.Exception e) { Guard.Swallow(e); }
                }
            }
            _scanned = true;
            ModRuntime.Log?.Msg("[WorldPickup] Scanned " + _byId.Count + " ItemPickup (WorldId)");
        }

        /// <summary>
        /// Prop by WorldId. Ids are cached per instance at scene load, so a miss is a prop instantiated after the
        /// scan (or one in another room/scene): rescan at most once per RescanCooldown, never per entry.
        /// Null for a destroyed prop.
        /// </summary>
        ItemPickup Find(ulong id)
        {
            EnsureScanned();
            ItemPickup p;
            if (!_byId.TryGetValue(id, out p) || p == null)
            {
                if (Time.unscaledTime - _lastRescanAt < RescanCooldown) return null;
                _scanned = false;
                EnsureScanned();
                _byId.TryGetValue(id, out p);
            }
            return Alive(p) ? p : null;
        }

        // ================================================================== host tick / send

        public void TickHost(LanNetworkManager net)
        {
            // Runs for every role (LanNetworkManager.Update): client deny reverts / release waits live here too.
            TickTakes();
            if (net == null || !NetGate.Host) return;
            if (!Config.ModConfig.WorldPickupsEnabled) return;

            // Send cadence is wall-clock: slow-mo / timeScale must not stretch the pickup state stream.
            _timer += Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (_timer < Interval && !_needFull) return;
            _timer = 0f;
            SendState(net);
        }

        /// <summary>
        /// Host: push pending prop changes to every peer now, outside the tick. The pre-unicast flush: a change still
        /// waiting for its broadcast must reach everyone before a join dump goes to one peer.
        /// </summary>
        public void FlushDiffNow()
        {
            var net = LanNetworkManager.Instance;
            if (!NetGate.Host || net.UnicastActive) return;
            if (!Config.ModConfig.WorldPickupsEnabled) return;
            bool full = _needFull;
            _needFull = false;
            SendState(net);
            _needFull = full;
        }

        void SendState(LanNetworkManager net)
        {
            float t0 = Time.realtimeSinceStartup;
            try
            {
                EnsureScanned();
                if (_byId.Count == 0) return;

                _tickList.Clear();
                bool full = _needFull;
                _needFull = false;
                // A unicast dump goes to one peer: send everything, but do not record it as what everyone has.
                bool record = !net.UnicastActive;

                foreach (var kvp in _byId)
                {
                    var p = kvp.Value;
                    ulong id = kvp.Key;
                    bool gone, triggered, active = false;
                    try
                    {
                        gone = p == null || p.gameObject == null;
                        triggered = gone || _claims.ContainsKey(id);
                        if (!gone)
                        {
                            // Native `triggered` stays true for the whole yes/no dialogue and until release ran. While
                            // this host's own release is pending it is NOT a claim: auto-claiming it here (without a
                            // claimer) would deny the host's own "yes" and strand the prop on a "no".
                            bool nativeTrig = p.triggered && !(_takes.Count > 0 && IsReleasePending(id));
                            triggered = nativeTrig || triggered;
                            active = p.gameObject.activeInHierarchy && p.enabled && !triggered;
                        }
                    }
                    catch
                    {
                        // Inactive EventObject props can throw; do not treat as claimed.
                        continue;
                    }

                    if (triggered) MarkClaimed(id, Items.itemlist.None);

                    Sent last;
                    _sent.TryGetValue(id, out last);
                    if (!full && last.Triggered == triggered && last.Active == active) continue;
                    int rem = 0;
                    if (!triggered && _withheld.Contains(id)) rem = WithheldCount;
                    else if (!triggered) _remainder.TryGetValue(id, out rem);
                    _tickList.Add(new WorldPickupEntry
                    {
                        WorldId = unchecked((long)id),
                        Triggered = triggered,
                        Active = active,
                        Count = rem
                    });
                    if (record) _sent[id] = new Sent { Triggered = triggered, Active = active };
                }

                if (_tickList.Count == 0) return;
                net.WorldPickupHandlers.SendWorldPickupState(_tickList, full);
                HideClaimed(null);
            }
            finally
            {
                HitchTrace.Cost("pickup", (Time.realtimeSinceStartup - t0) * 1000f);
            }
        }

        /// <summary>One out-of-tick entry to every peer (recorded as sent unless inside a unicast dump).</summary>
        void SendOne(ulong id, bool triggered, int count = 0)
        {
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            net.WorldPickupHandlers.SendWorldPickupState(new[]
            {
                new WorldPickupEntry { WorldId = unchecked((long)id), Triggered = triggered, Active = !triggered, Count = count }
            }, false);
            if (!net.UnicastActive) _sent[id] = new Sent { Triggered = triggered, Active = !triggered };
        }

        // ================================================================== host claim

        /// <summary>
        /// Host: reserve a pickup for claimer.
        /// hideNow=false when host is about to run native pickUp() itself.
        /// </summary>
        public bool TryClaimOnHost(ulong worldId, int claimerPlayerId, out Items.itemlist itemEnum, out int count,
            bool hideNow = true, Items.itemlist hintItem = Items.itemlist.None, int hintCount = 0)
        {
            itemEnum = hintItem;
            count = hintCount > 0 ? hintCount : 1;
            EnsureScanned();

            Claim held;
            if (_claims.TryGetValue(worldId, out held))
            {
                if (held.Claimer != NoClaimer)
                {
                    if (held.Claimer != claimerPlayerId) return false;
                    HideClaimed(null);
                    return true;
                }
                // Claimed with no claimer on record = the world marked it (native `triggered` seen by the tick,
                // or the prop is gone). Only the host's own in-flight pickup may take that over.
                if (!IsLocalHost(claimerPlayerId)) return false;
                _claims.Remove(worldId);
            }

            var p = Find(worldId);
            if (p != null)
            {
                var resolved = ResolveItem(p);
                if (resolved != Items.itemlist.None) itemEnum = resolved;
                try { if (p.count > 0) count = p.count; } catch (System.Exception e) { Guard.Swallow(e); }
            }
            else if (itemEnum == Items.itemlist.None)
            {
                // Other room: still reserve the WorldId so the prop hides when the chunk wakes.
                PlaytestLog.Event("Pickup", "claim without local prop id=" + worldId.ToString("X16") + " hint=" + hintItem);
            }

            if (HoldTarotDeathForSacrifice(itemEnum))
            {
                PlaytestLog.Event("Pickup", "deny TarotDeath — KeyOfSacrifice still available");
                return false;
            }

            _claims[worldId] = new Claim { Claimer = claimerPlayerId, Item = itemEnum };
            NoteClaimedItem(itemEnum);

            if (hideNow)
            {
                if (p != null) HidePickup(p);
                HideClaimed(null);
            }
            return true;
        }

        /// <summary>
        /// Host: decide a claim and publish it — the one path for the host's own native take (hostNative: pickUp /
        /// release add the item and already ran onPickup) and a client's claim (grant message). Claimed props hide
        /// for everyone and a unique goes on the party key ring.
        /// </summary>
        public bool HostClaim(ulong id, int claimer, Items.itemlist hintItem, int hintCount, bool hostNative)
        {
            Items.itemlist item;
            int count;
            if (!TryClaimOnHost(id, claimer, out item, out count, hideNow: true, hintItem: hintItem, hintCount: hintCount))
                return false;
            var net = LanNetworkManager.Instance;
            if (hostNative)
                NoteOnPickupFired(id); // native pickUp already Invoked onPickup: the broadcast's Ensure must not re-fire (Dig H)
            else
                ModRuntime.Log?.Msg("[WorldPickup] Claim OK id=" + id.ToString("X16") + " item=" + item + " x" + count
                    + " → player " + claimer);
            PartyKeyRing.Note(item);
            PartyKeyRing.Broadcast();
            if (!hostNative && net != null)
            {
                if (item != Items.itemlist.None)
                    net.WorldPickupHandlers.SendWorldPickupGrant(claimer, id, item, count > 0 ? count : 1);
                else
                    ModRuntime.Log?.Warning("[WorldPickup] Claim OK but item None id=" + id.ToString("X16"));
            }
            BroadcastTriggered(id, true);
            return true;
        }

        /// <summary>
        /// Undo a host Prefix reservation when native pickUp refused (nospace / cancel).
        /// Only the same claimer may release — peer claims stay.
        /// </summary>
        public bool ReleaseClaimIf(ulong worldId, int claimerPlayerId)
        {
            Claim held;
            if (!_claims.TryGetValue(worldId, out held)) return false;
            if (held.Claimer != NoClaimer)
            {
                if (held.Claimer != claimerPlayerId) return false;
            }
            else if (!IsLocalHost(claimerPlayerId))
                return false; // unowned claim: only the host's own pickup may give it back
            ForgetClaim(worldId);
            PlaytestLog.Event("Pickup", "release claim id=" + worldId.ToString("X16")
                + " by=" + claimerPlayerId + " item=" + held.Item);
            return true;
        }

        /// <summary>Host declined its own yes/no: free the claim and tell peers to show the prop again.</summary>
        public void ReleaseAndBroadcast(ulong worldId, int claimerPlayerId)
        {
            if (!ReleaseClaimIf(worldId, claimerPlayerId)) return;
            Take t;
            if (_takes.TryGetValue(worldId, out t)) t.MeasureGain = false;
            SendOne(worldId, false);
            ItemPickup p;
            if (_byId.TryGetValue(worldId, out p) && p != null) RestorePickup(p);
        }

        /// <summary>
        /// Host: the claimer's native take left `remaining` on the prop (bag full). Free its claim and show every
        /// peer the prop with that count; only the claim's owner may do this.
        /// </summary>
        public void HostReleaseRemainder(ulong worldId, int claimerPlayerId, int remaining)
        {
            if (remaining <= 0) return;
            if (!ReleaseClaimIf(worldId, claimerPlayerId))
            {
                PlaytestLog.Event("Pickup", "remainder ignored id=" + worldId.ToString("X16") + " from=" + claimerPlayerId
                    + " (not its claim)");
                return;
            }
            _remainder[worldId] = remaining;
            var p = Find(worldId);
            if (p != null)
            {
                SetPropCount(p, remaining);
                if (AlreadyHidden(p)) RestorePickup(p);
            }
            SendOne(worldId, false, remaining);
            PlaytestLog.Event("Pickup", "remainder x" + remaining + " back on prop id=" + worldId.ToString("X16")
                + " (from p" + claimerPlayerId + ")");
        }

        public void BroadcastTriggered(ulong worldId, bool triggered)
        {
            if (LanNetworkManager.Instance == null) return;
            ItemPickup p;
            if (!_byId.TryGetValue(worldId, out p) || !Alive(p)) p = null;
            MarkClaimed(worldId, ItemOfProp(p));
            // Host does not ApplyHide its own broadcast — party onPickup for host when
            // a client claimed (native pickUp never ran here). Host-native path Notes
            // first so this Ensure is a no-op (Dig H).
            if (triggered)
                EnsurePartyOnPickup(worldId, p);
            SendOne(worldId, triggered);
        }

        // ================================================================== client apply

        public void ApplyHide(WorldPickupStateMessage msg)
        {
            if (msg.Entries == null) return;
            EnsureScanned();

            for (int i = 0; i < msg.Entries.Length; i++)
            {
                var e = msg.Entries[i];
                ulong id = unchecked((ulong)e.WorldId);
                if (id == 0) continue;
                var p = Find(id);

                if (!e.Triggered && e.Count == WithheldCount)
                {
                    // The host's DynamicSupply withheld it (the party is stocked): off here too, no onPickup.
                    if (p != null)
                    {
                        try { p.gameObject.SetActive(false); } catch (System.Exception ex) { Guard.Swallow(ex); }
                        PlaytestLog.Verbose("Pickup", "withheld id=" + id.ToString("X16"));
                    }
                    continue;
                }
                if (!e.Triggered)
                {
                    // Host gave the claim back (declined yes/no / partial take / rollback) — restore the prop. Only
                    // undo a hide this peer did: the join dump lists every unclaimed prop as Triggered=false, and
                    // force-activating them revealed props the level keeps off (PEN_Cryo.contentLateActivated: the
                    // cryo key card, live before the pod opened).
                    Take t;
                    bool wasHidden = _claims.ContainsKey(id) || (_takes.TryGetValue(id, out t) && t.HideHeld);
                    ForgetClaim(id);
                    if (p == null) continue;
                    if (e.Count > 0) SetPropCount(p, e.Count);
                    if (wasHidden)
                    {
                        RestorePickup(p);
                        PlaytestLog.Verbose("Pickup", "unhide id=" + id.ToString("X16") + (e.Count > 0 ? " x" + e.Count : ""));
                    }
                    continue;
                }

                MarkClaimed(id, ItemOfProp(p));
                // Triggered rising (or FullRefresh remount): party onPickup for non-claimers
                // (TakeCard/takeRing/takeSpear/StartOutro/MeatBlocker). Invoke before Hide
                // so persistent GameObject args stay live. HashSet de-dupes Grant+Hide.
                EnsurePartyOnPickup(id, p);
                if (p != null)
                {
                    HidePickup(p);
                    string name = "?";
                    try { name = p.gameObject.name; } catch (System.Exception ex) { Guard.Swallow(ex); }
                    PlaytestLog.Verbose("Pickup", "hide id=" + id.ToString("X16") + " " + name);
                }
                else
                    PlaytestLog.Verbose("Pickup", "hide pending id=" + id.ToString("X16"));
            }

            HideClaimed(null);
        }

        /// <summary>Client-side inventory grant after host approved claim.</summary>
        public void ApplyGrant(WorldPickupGrantMessage msg)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || msg.TargetPlayerId != net.LocalPlayerId) return;

            ulong id = unchecked((ulong)msg.WorldId);
            var kind = (Items.itemlist)msg.ItemEnum;
            try
            {
                var p = Find(id);
                MarkClaimed(id, kind);

                NetGate.BeginApply();
                try
                {
                    AnItem item = kind != Items.itemlist.None ? InventoryManager.getItem(kind) : null;
                    if (item == null && p != null)
                    {
                        kind = ResolveItem(p);
                        if (kind != Items.itemlist.None) item = InventoryManager.getItem(kind);
                        if (item == null) item = p._item;
                    }
                    if (item == null || kind == Items.itemlist.None)
                    {
                        ModRuntime.Log?.Warning("[WorldPickup] Grant unknown item " + msg.ItemEnum);
                        return;
                    }
                    // Inspect-path claims: native pickUp/release already added (or is about to add)
                    // the item — AddItem here would double it. Every other claim (client Prefix blocked
                    // native pickUp) must AddItem even if the bag already holds a stack of it, otherwise
                    // ammo/health/batteries are hidden for all peers and granted to nobody.
                    Take t;
                    bool nativeAdds = _takes.TryGetValue(id, out t) && t.NativeAdds;
                    if (nativeAdds)
                    {
                        // Verdict in: the take only waits for its release now (gain check / held hide).
                        t.NativeAdds = false;
                        if (t.Phase == Phase.AwaitVerdict) _takes.Remove(id);
                        // Native release owns the add (and its onPickup Invoke): party onPickup must not fire twice.
                        NoteOnPickupFired(id);
                    }
                    else
                    {
                        // Non-inspect props only (the BOS_Adler spears, count 1; the claim Prefix checked bag room).
                        int grantCount = msg.Count > 0 ? msg.Count : 1;
                        int before = ItemBag.CountInBag(kind);
                        InventoryManager.AddItem(item, grantCount);
                        // AddItem silently caps at maxNumber: measure what the bag really took.
                        int gained = ItemBag.CountInBag(kind) - before;
                        if (gained < grantCount)
                            PlaytestLog.Warn("Pickup", "grant " + kind + " took " + (gained > 0 ? gained : 0)
                                + "/" + grantCount + " (stack cap)");
                    }
                    PartyKeyRing.Note(item);
                    PartyKeyRing.BindUseDialogue(item);

                    // Client Prefix blocks ItemPickup.pickUp, so onPickup never ran. Host + non-claimers Ensure via
                    // ApplyHide/BroadcastTriggered; the claimer uses the same Ensure (HashSet de-dupes). Dig H / 0.5.24.
                    EnsurePartyOnPickup(id, p);
                }
                finally
                {
                    NetGate.EndApply();
                }

                if (p != null)
                {
                    // Held while this peer's native release is still pending (inspect claims).
                    HidePickup(p);
                    PartyKeyRing.Note(p._item);
                }
                ModRuntime.Log?.Msg("[WorldPickup] Granted " + (Items.itemlist)msg.ItemEnum + " x" + msg.Count);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.Warning("[WorldPickup] Grant failed: " + ex.Message);
            }
        }

        // ================================================================== party onPickup

        /// <summary>
        /// Host-native pickUp already Invoked onPickup — mark so BroadcastTriggered /
        /// ApplyHide Ensure do not double-fire (Dig H).
        /// </summary>
        public void NoteOnPickupFired(ulong worldId)
        {
            if (worldId != 0) _partyOnPickupFired.Add(worldId);
        }

        /// <summary>
        /// Idempotent party onPickup: once per WorldId until scene refresh / claim release.
        /// Skips when prop not yet scanned (pending other-room) so a later ApplyHide can fire.
        /// Fires the story/world listeners without re-running pickUp (which would AddItem again).
        /// </summary>
        public bool EnsurePartyOnPickup(ulong worldId, ItemPickup p)
        {
            if (worldId == 0 || p == null || _partyOnPickupFired.Contains(worldId)) return false;
            NetGate.BeginApply();
            try
            {
                p.onPickup?.Invoke();
            }
            catch (System.Exception ex)
            {
                // Marked fired even on a throw: listeners that already ran (StartOutro before UnJam) must not run twice.
                ModRuntime.Log?.Warning("[WorldPickup] onPickup Invoke: " + ex.Message);
            }
            finally
            {
                NetGate.EndApply();
            }
            _partyOnPickupFired.Add(worldId);
            return true;
        }
    }
}
