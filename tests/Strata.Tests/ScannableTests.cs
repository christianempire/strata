using Strata.Core.Model;
using Strata.Host;

namespace Strata.Tests;

/// <summary>
/// Which volumes Strata touches without being asked.
/// </summary>
/// <remarks>
/// One predicate decides four things — what <c>strata volumes</c> counts as eligible, what
/// <c>strata scan --all</c> walks, what the API reports as <c>scannable</c>, and what the resident
/// scheduler ticks — so it is worth pinning rather than trusting to a reading of the code.
/// </remarks>
public sealed class ScannableTests
{
    /// <summary>
    /// EFI and recovery partitions are left alone.
    /// </summary>
    /// <remarks>
    /// Windows keeps them unmounted on purpose. The cost of including them was not the scan but the
    /// audit log: the scheduler attempts every watchable volume on every tick, so two partitions
    /// nobody will ever baseline were writing two "no baseline yet" rows into scan_log every fifteen
    /// minutes, forever, on the one table that exists to be read by a person.
    /// </remarks>
    [Fact]
    public void AVolumeWithNoDriveLetterIsNotScannedUnprompted()
    {
        Assert.False(VolumesCommand.IsScannable(Volume(letter: null)));
        Assert.False(VolumesCommand.IsScannable(Volume(letter: null, fileSystem: "FAT32")));
    }

    [Fact]
    public void AnOrdinaryFixedVolumeStillIs()
    {
        Assert.True(VolumesCommand.IsScannable(Volume(letter: "C:")));
    }

    [Fact]
    public void RemovableAndNetworkVolumesAreStillExcluded()
    {
        Assert.False(VolumesCommand.IsScannable(Volume(letter: "F:", kind: VolumeKind.Removable)));
        Assert.False(VolumesCommand.IsScannable(Volume(letter: "Z:", kind: VolumeKind.Network)));
    }

    [Fact]
    public void AVolumeWithNoMediaIsStillExcluded()
    {
        Assert.False(VolumesCommand.IsScannable(Volume(letter: "D:", totalBytes: 0)));
    }

    private static VolumeIdentity Volume(
        string? letter,
        VolumeKind kind = VolumeKind.Fixed,
        string fileSystem = "NTFS",
        long totalBytes = 1024L * 1024 * 1024 * 512) => new()
    {
        GuidPath = @"\?\Volume{11111111-2222-3333-4444-555555555555}\",
        DriveLetter = letter,
        Label = "",
        FileSystem = fileSystem,
        Kind = kind,
        TotalBytes = totalBytes,
        FreeBytes = totalBytes / 2,
    };
}
