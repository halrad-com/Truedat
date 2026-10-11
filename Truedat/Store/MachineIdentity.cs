using System;

namespace Truedat.Store
{
    /// <summary>The store's own machine id (spec §3, G8): minted on first open and kept IN the store,
    /// with the hostname beside it. Opened on another host it keeps the id but says so instead of
    /// silently becoming that machine — a copied store must not impersonate its source.</summary>
    internal sealed class MachineIdentity
    {
        /// <summary>The machine that created the store.</summary>
        internal string Id { get; }
        internal string CreatedByHost { get; }
        internal string CurrentHost { get; }
        internal bool HostMatches => string.Equals(CreatedByHost, CurrentHost, StringComparison.OrdinalIgnoreCase);

        MachineIdentity(string id, string createdByHost, string currentHost) { Id = id; CreatedByHost = createdByHost; CurrentHost = currentHost; }

        internal static MachineIdentity EnsureMinted(StoreDal dal, string currentHost, string nowUtc)
        {
            var id = dal.MetaGet("machine_id");
            if (id == null)
            {
                id = Uuid7.New();
                dal.InsertMachine(id, currentHost, nowUtc);
                dal.MetaSet("machine_id", id);
                dal.MetaSet("created_by_host", currentHost);
                dal.MetaSet("created_utc", nowUtc);
                StoreLog.Info($"machine id minted: {id} on {currentHost}");
                return new MachineIdentity(id, currentHost, currentHost);
            }
            var created = dal.MetaGet("created_by_host") ?? "?";
            var mi = new MachineIdentity(id, created, currentHost);
            if (!mi.HostMatches)
                StoreLog.Warn($"this store was created on host {created} and is open on {currentHost}; reads are fine, writes that record locations as this machine need --machine-adopt");
            return mi;
        }

        /// <summary>The machine id writes must be recorded under. On the minting host that is the store's
        /// own id. On another host it is a refusal unless --machine-adopt, and adopting means finding or
        /// MINTING a machine row for the current host — never writing the current host's roots under the
        /// creating machine's id (review #2: that collapsed two machines' D:\Music into one root).</summary>
        internal string ForWrites(StoreDal dal, bool adopt, string nowUtc)
        {
            if (HostMatches) return Id;
            if (!adopt)
                throw new StoreException(
                    $"This store was created on '{CreatedByHost}' and is being written from '{CurrentHost}'. " +
                    "Pass --machine-adopt to record locations as this machine, or open the store on its own host. Nothing was changed.");
            var existing = dal.FindMachineByHost(CurrentHost);
            if (existing != null) return existing;
            var id = Uuid7.New();
            dal.InsertMachine(id, CurrentHost, nowUtc);
            StoreLog.Info($"machine id minted for adopting host {CurrentHost}: {id} (store created on {CreatedByHost})");
            return id;
        }
    }
}
