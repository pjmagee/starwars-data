using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.Tests;

public class CharacterTimelineTrackerTests
{
    private readonly CharacterTimelineTracker _tracker = new();

    [Fact]
    public void TryStart_NewPageId_ReturnsTrue()
    {
        Assert.True(_tracker.TryStart(1));
    }

    [Fact]
    public void TryStart_DuplicatePageId_ReturnsFalse()
    {
        _tracker.TryStart(1);
        Assert.False(_tracker.TryStart(1));
    }

    [Fact]
    public void TryStart_SetsQueuedStage()
    {
        _tracker.TryStart(1);

        var status = _tracker.GetStatus(1);
        Assert.NotNull(status);
        Assert.Equal(GenerationStage.Queued, status.Stage);
    }

    [Fact]
    public void GetStatus_UnknownPageId_ReturnsNull()
    {
        Assert.Null(_tracker.GetStatus(999));
    }

    [Fact]
    public void Update_ChangesStageAndMessage()
    {
        _tracker.TryStart(1);
        _tracker.Update(1, GenerationStage.Extracting, "Processing 5 pages...");

        var status = _tracker.GetStatus(1);
        Assert.NotNull(status);
        Assert.Equal(GenerationStage.Extracting, status.Stage);
        Assert.Equal("Processing 5 pages...", status.Message);
    }

    [Fact]
    public void Complete_SetsCompleteStage()
    {
        _tracker.TryStart(1);
        _tracker.Complete(1, "Done! 42 events");

        var status = _tracker.GetStatus(1);
        Assert.NotNull(status);
        Assert.Equal(GenerationStage.Complete, status.Stage);
        Assert.Equal("Done! 42 events", status.Message);
    }

    [Fact]
    public void Fail_SetsFailedStageAndError()
    {
        _tracker.TryStart(1);
        _tracker.Fail(1, "OpenAI timeout");

        var status = _tracker.GetStatus(1);
        Assert.NotNull(status);
        Assert.Equal(GenerationStage.Failed, status.Stage);
        Assert.Equal("OpenAI timeout", status.Error);
    }

    [Fact]
    public void IsRunning_ActiveStages_ReturnsTrue()
    {
        _tracker.TryStart(1);
        Assert.True(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Discovering, "...");
        Assert.True(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Extracting, "...");
        Assert.True(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Reviewing, "...");
        Assert.True(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Saving, "...");
        Assert.True(_tracker.IsRunning(1));
    }

    [Fact]
    public void IsRunning_TerminalStages_ReturnsFalse()
    {
        _tracker.TryStart(1);
        _tracker.Complete(1, "done");
        Assert.False(_tracker.IsRunning(1));

        _tracker.TryStart(2);
        _tracker.Fail(2, "error");
        Assert.False(_tracker.IsRunning(2));
    }

    [Fact]
    public void IsRunning_UnknownPageId_ReturnsFalse()
    {
        Assert.False(_tracker.IsRunning(999));
    }

    [Fact]
    public void Clear_RemovesStatus()
    {
        _tracker.TryStart(1);
        _tracker.Clear(1);

        Assert.Null(_tracker.GetStatus(1));
        Assert.False(_tracker.IsRunning(1));
    }
}
