namespace Franthropy.Dalamud.Automation.Vendors;

internal sealed class GilVendorAetheryteSnapshot
{
    private IReadOnlySet<uint>? aetheryteIds;
    private ulong ownerContentId;

    public void SynchronizeOwner(ulong contentId)
    {
        if (ownerContentId == contentId)
            return;

        ownerContentId = contentId;
        aetheryteIds = null;
    }

    public void Observe(ulong contentId, IEnumerable<uint> observedAetheryteIds)
    {
        ArgumentNullException.ThrowIfNull(observedAetheryteIds);
        SynchronizeOwner(contentId);
        aetheryteIds = new HashSet<uint>(observedAetheryteIds.Where(id => id != 0));
    }

    public bool TryRead(ulong contentId, out IReadOnlySet<uint> observedAetheryteIds)
    {
        SynchronizeOwner(contentId);
        if (aetheryteIds is null)
        {
            observedAetheryteIds = new HashSet<uint>();
            return false;
        }

        observedAetheryteIds = aetheryteIds;
        return true;
    }
}
