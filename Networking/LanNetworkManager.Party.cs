namespace SyncRADation.Networking
{
    /// <summary>Party save + room report wire ownership.</summary>
    public sealed partial class LanNetworkManager
    {
        private PartyNetHandlers _partyHandlers;

        internal PartyNetHandlers PartyHandlers =>
            _partyHandlers ?? (_partyHandlers = new PartyNetHandlers(this));

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
