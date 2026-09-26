using LoRAMancer.App.Models;
using LoRAMancer.App.Services;
using Xunit;

namespace LoRAMancer.Tests;

public class TrainingQueueTests {
    [Fact]
    public void EnqueueJob_AddsJobToQueue_WithCorrectProperties() {
        var runner = new TrainingRunnerService();
        string dummyConfig = Path.Combine(Path.GetTempPath(), "test_model_aitoolkit.yaml");
        File.WriteAllText(dummyConfig, "name: test");

        try {
            var job = runner.EnqueueJob(dummyConfig, "dummy_venv");
            Assert.NotNull(job);
            Assert.Equal("test_model", job.Name);
            Assert.Equal(dummyConfig, job.ConfigYamlPath);
            Assert.Equal("dummy_venv", job.VenvPath);
        } finally {
            runner.ClearQueue();
            if (File.Exists(dummyConfig)) {
                File.Delete(dummyConfig);
            }
        }
    }

    [Fact]
    public void QueueOperations_ReorderAndRemove_WorkCorrectly() {
        var runner = new TrainingRunnerService();
        string dummyConfig1 = Path.Combine(Path.GetTempPath(), "job1_aitoolkit.yaml");
        string dummyConfig2 = Path.Combine(Path.GetTempPath(), "job2_aitoolkit.yaml");
        string dummyConfig3 = Path.Combine(Path.GetTempPath(), "job3_aitoolkit.yaml");

        File.WriteAllText(dummyConfig1, "name: 1");
        File.WriteAllText(dummyConfig2, "name: 2");
        File.WriteAllText(dummyConfig3, "name: 3");

        try {
            var job1 = runner.EnqueueJob(dummyConfig1);
            var job2 = runner.EnqueueJob(dummyConfig2);
            var job3 = runner.EnqueueJob(dummyConfig3);

            // Reordering test
            runner.MoveJobUp(job3.Id);
            runner.MoveJobDown(job1.Id);

            // Removal test
            bool removed = runner.RemoveJob(job2.Id);
            Assert.True(removed);
        } finally {
            runner.ClearQueue();
            File.Delete(dummyConfig1);
            File.Delete(dummyConfig2);
            File.Delete(dummyConfig3);
        }
    }
}
