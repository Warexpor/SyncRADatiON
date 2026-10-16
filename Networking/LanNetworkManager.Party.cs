namespace SyncRADation.Networking
{
    /// <summary>Party life (downed / revive / wipe) + party save wire ownership.</summary>
    public sealed partial class LanNetworkManager
    {
        private PartyNetHandlers _partyHandlers;

        internal PartyNetHandlers PartyHandlers =>
            _partyHandlers ?? (_partyHandlers = new PartyNetHandlers(this));

        public void SendPartyLife(PartyLifeMessage msg) => PartyHandlers.SendPartyLife(msg);

        /// <summary>Last scene this peer announced (host/local included). "" when unknown.</summary>
        internal string SceneOf(int playerId)
        {
            if (playerId == _localPlayerId)
                return _localSceneName ?? "";
            if (playerId == 0)
                return _hostSceneName ?? "";
            string scene;
            return _peerScenes.TryGetValue(playerId, out scene) ? (scene ?? "") : "";
        }
    }
}
