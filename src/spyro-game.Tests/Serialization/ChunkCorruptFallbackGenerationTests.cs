using SpyroGame.Shared.State;
using SpyroGame.World;
using System.IO.Compression;
using System.Reflection;
using Xunit;
using ChunkStreamingManager = SpyroGame.Server.Streaming.ChunkStreamingManager;

namespace SpyroGame.Tests.Serialization;

public class ChunkCorruptFallbackGenerationTests
{
    [Fact]
    public void CorruptChunkStateFile_FallsBackToGeneration_AndDoesNotStall()
    {
        var originalCwd = Environment.CurrentDirectory;
        var testRoot = Path.Combine(Path.GetTempPath(), $"spyro_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);

        try
        {
            // Arrange: isolate data root so the streaming manager indexes only our test files.
            Environment.CurrentDirectory = testRoot;

            var seed = 1;
            var world = new VoxelWorld(seed);
            using var mgr = new ChunkStreamingManager(world);
            mgr.Initialize(seed);

            var playerId = PlayerId.New();
            var neededChunkIdx = 0;

            // Create a corrupt on-disk chunk state file for chunk 0.
            // The indexer will see it as "known on disk" but loading it should fail.
            WriteCorruptChunkStateFile(testRoot, worldName: "DefaultWorld", seed, neededChunkIdx);

            // Re-index now that the file exists.
            // (IndexChunkStateFilesOnDisk is private, so call Initialize again.)
            mgr.Initialize(seed);

            // Inject a tiny desired set (only one chunk) without registering a player position.
            // Tick() will not overwrite desired sets if there are no playerPositions.
            SetDesiredChunksByPlayer(mgr, playerId, new HashSet<int> { neededChunkIdx });

            // Act: tick until either the chunk is ready (after regen) or we time out.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                mgr.Tick();

                if (mgr.IsChunkReadyForPlayer(playerId, neededChunkIdx))
                {
                    return; // success
                }

                Thread.Sleep(10);
            }

            // Assert
            Assert.Fail("Chunk did not become ready; corrupt save likely stalled generation.");
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            try
            {
                if (Directory.Exists(testRoot))
                {
                    Directory.Delete(testRoot, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static void SetDesiredChunksByPlayer(ChunkStreamingManager manager, PlayerId playerId, HashSet<int> desired)
    {
        var field = typeof(ChunkStreamingManager).GetField("desiredChunksByPlayer", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);

        var dict = (Dictionary<PlayerId, HashSet<int>>)field!.GetValue(manager)!;
        dict[playerId] = desired;
    }

    private static void WriteCorruptChunkStateFile(string dataRoot, string worldName, int seed, int chunkIdx)
    {
        var chunkPos = VoxelHelper.GetChunkPositionGlobal(chunkIdx);
        var folderName = $"{worldName}_{seed}";
        var dirPath = Path.Combine(dataRoot, "save", folderName);
        Directory.CreateDirectory(dirPath);

        var filePath = Path.Combine(dirPath, $"chunk_{chunkPos.X}_{chunkPos.Z}{ChunkStreamingManager.ChunkSaveFileExtension}");

        // Header matches expected magic/version. Body is gzipped but contains garbage.
        using var fs = File.Create(filePath);
        using (var headerWriter = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            headerWriter.Write("CHNK");
            headerWriter.Write(2);
        }

        using (var gzip = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true))
        {
            var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            gzip.Write(junk, 0, junk.Length);
        }
    }
}
