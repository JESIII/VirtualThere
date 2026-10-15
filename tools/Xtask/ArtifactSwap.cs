// SPDX-License-Identifier: Apache-2.0
//
// Publishing into an artifact directory without ever leaving it half-populated.
//
// The build assembles the complete new set in a staging directory and then swaps it in with two
// renames on one volume. Nothing is ever written into the live directory, so at every point either
// the previous set is there, or the new one is: there is no state in which dist/portable exists and
// is missing files. Deleting the live directory first - what this replaces - has no such property,
// which is why a single locked executable cost the operator their working install.

namespace Xtask;

/// <summary>Replaces a directory with an already-assembled one, or leaves it alone.</summary>
internal static class ArtifactSwap
{
    /// <summary>
    /// Moves <paramref name="staging"/> onto <paramref name="destination"/>, parking the current
    /// contents at <paramref name="previous"/> for the length of the swap and putting them back if
    /// the second rename fails. <paramref name="staging"/>, <paramref name="destination"/> and
    /// <paramref name="previous"/> must be on one volume (they are: all three live under dist/).
    /// </summary>
    internal static void Replace(string staging, string destination, string previous, string description)
    {
        bool hadPrevious = Directory.Exists(destination);
        BuildContext.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);

        if (hadPrevious)
        {
            // A leftover from an interrupted swap, not an artifact.
            BuildContext.DeleteDirectory(previous);
            BuildContext.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(previous))!);

            try
            {
                Directory.Move(destination, previous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"could not move {destination} aside ({ex.Message}). Nothing has been published and the " +
                    "existing set is unchanged.", ex);
            }
        }

        try
        {
            Directory.Move(staging, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"could not move {staging} into place ({ex.Message}). {Restore(hadPrevious, previous, destination)}", ex);
        }

        BuildContext.TryDeleteDirectory(previous);
    }

    /// <summary>
    /// Puts the parked previous set back. Called only when the swap itself failed, so the live
    /// directory is never left missing; if even this fails, the message says where the old set is
    /// rather than pretending it is gone.
    /// </summary>
    private static string Restore(bool hadPrevious, string previous, string destination)
    {
        if (!hadPrevious)
        {
            return $"Nothing was published and {destination} does not exist yet.";
        }

        if (Directory.Exists(destination))
        {
            return $"{destination} still holds the previous set.";
        }

        try
        {
            Directory.Move(previous, destination);
            return $"The previous set was moved back to {destination} and is intact.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The previous set could not be moved back ({ex.Message}); it is intact at {previous}.";
        }
    }
}
