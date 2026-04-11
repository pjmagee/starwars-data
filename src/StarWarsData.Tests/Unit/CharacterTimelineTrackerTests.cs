using StarWarsData.Models.Entities;
using StarWarsData.Services;

namespace StarWarsData.Tests.Unit;

[TestClass]
[TestCategory(TestTiers.Unit)]
public class CharacterTimelineTrackerTests
{
    private readonly CharacterTimelineTracker _tracker = new();

    [TestMethod]
    public void TryStart_NewPageId_ReturnsTrue()
    {
        Assert.IsTrue(_tracker.TryStart(1));
    }

    [TestMethod]
    public void TryStart_DuplicatePageId_ReturnsFalse()
    {
        _tracker.TryStart(1);
        Assert.IsFalse(_tracker.TryStart(1));
    }

    [TestMethod]
    public void TryStart_SetsQueuedStage()
    {
        _tracker.TryStart(1);

        var status = _tracker.GetStatus(1);
        Assert.IsNotNull(status);
        Assert.AreEqual(GenerationStage.Queued, status.Stage);
    }

    [TestMethod]
    public void GetStatus_UnknownPageId_ReturnsNull()
    {
        Assert.IsNull(_tracker.GetStatus(999));
    }

    [TestMethod]
    public void Update_ChangesStageAndMessage()
    {
        _tracker.TryStart(1);
        _tracker.Update(1, GenerationStage.Extracting, "Processing 5 pages...");

        var status = _tracker.GetStatus(1);
        Assert.IsNotNull(status);
        Assert.AreEqual(GenerationStage.Extracting, status.Stage);
        Assert.AreEqual("Processing 5 pages...", status.Message);
    }

    [TestMethod]
    public void Complete_SetsCompleteStage()
    {
        _tracker.TryStart(1);
        _tracker.Complete(1, "Done! 42 events");

        var status = _tracker.GetStatus(1);
        Assert.IsNotNull(status);
        Assert.AreEqual(GenerationStage.Complete, status.Stage);
        Assert.AreEqual("Done! 42 events", status.Message);
    }

    [TestMethod]
    public void Fail_SetsFailedStageAndError()
    {
        _tracker.TryStart(1);
        _tracker.Fail(1, "OpenAI timeout");

        var status = _tracker.GetStatus(1);
        Assert.IsNotNull(status);
        Assert.AreEqual(GenerationStage.Failed, status.Stage);
        Assert.AreEqual("OpenAI timeout", status.Error);
    }

    [TestMethod]
    public void IsRunning_ActiveStages_ReturnsTrue()
    {
        _tracker.TryStart(1);
        Assert.IsTrue(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Discovering, "...");
        Assert.IsTrue(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Extracting, "...");
        Assert.IsTrue(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Reviewing, "...");
        Assert.IsTrue(_tracker.IsRunning(1));

        _tracker.Update(1, GenerationStage.Saving, "...");
        Assert.IsTrue(_tracker.IsRunning(1));
    }

    [TestMethod]
    public void IsRunning_TerminalStages_ReturnsFalse()
    {
        _tracker.TryStart(1);
        _tracker.Complete(1, "done");
        Assert.IsFalse(_tracker.IsRunning(1));

        _tracker.TryStart(2);
        _tracker.Fail(2, "error");
        Assert.IsFalse(_tracker.IsRunning(2));
    }

    [TestMethod]
    public void IsRunning_UnknownPageId_ReturnsFalse()
    {
        Assert.IsFalse(_tracker.IsRunning(999));
    }

    [TestMethod]
    public void Clear_RemovesStatus()
    {
        _tracker.TryStart(1);
        _tracker.Clear(1);

        Assert.IsNull(_tracker.GetStatus(1));
        Assert.IsFalse(_tracker.IsRunning(1));
    }
}
