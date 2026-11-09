namespace AdbSync.Core.Models.Config;

public enum ConflictPolicy
{
    /// <summary>The newer side wins automatically; the loser is backed up.</summary>
    NewestWins,

    /// <summary>A conflicting file is left alone on both sides until someone picks a version (PC or phone).</summary>
    Ask,
}
