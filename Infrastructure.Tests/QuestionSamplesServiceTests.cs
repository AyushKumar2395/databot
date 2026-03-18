using Application.Common.Interfaces;
using Application.Common.Models;
using Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Infrastructure.Tests;

public sealed class QuestionSamplesServiceTests
{
    [Fact]
    public async Task GetAsync_Returns_Grouped_And_Ordered_Response()
    {
        var repo = new FakeQuestionSamplesRepository(
        [
            new() { Id = 1, Environment = "SqlServer_Live", GroupKey = "Databases", GroupTitle = "Databases", GroupOrder = 10, QuestionText = "List all databases", QuestionOrder = 1, Tags = "Databases" },
            new() { Id = 2, Environment = "SqlServer_Live", GroupKey = "Databases", GroupTitle = "Databases", GroupOrder = 10, QuestionText = "Show database sizes", QuestionOrder = 2, Tags = "Databases,Size" },
            new() { Id = 3, Environment = "SqlServer_Live", GroupKey = "Backups", GroupTitle = "Backups & Recovery", GroupOrder = 20, QuestionText = "Show backup history", QuestionOrder = 1, Tags = "Backups" },
        ]);
        var service = CreateService(repo);

        var response = await service.GetAsync("SqlServer_Live", false, CancellationToken.None);

        Assert.Equal("SqlServer_Live", response.Environment);
        Assert.Equal(2, response.Groups.Count);

        var dbGroup = response.Groups[0];
        Assert.Equal("Databases", dbGroup.GroupKey);
        Assert.Equal("Databases", dbGroup.GroupTitle);
        Assert.Equal(10, dbGroup.Order);
        Assert.Equal(2, dbGroup.Items.Count);
        Assert.Equal(1, dbGroup.Items[0].Id);
        Assert.Equal("List all databases", dbGroup.Items[0].Question);
        Assert.Equal(new[] { "Databases" }, dbGroup.Items[0].Tags);
        Assert.Equal(2, dbGroup.Items[1].Id);
        Assert.Equal(new[] { "Databases", "Size" }, dbGroup.Items[1].Tags);

        var backupGroup = response.Groups[1];
        Assert.Equal("Backups", backupGroup.GroupKey);
        Assert.Equal("Backups & Recovery", backupGroup.GroupTitle);
        Assert.Equal(20, backupGroup.Order);
        Assert.Single(backupGroup.Items);
    }

    [Fact]
    public async Task GetAsync_Caches_Response_And_Returns_Same_Instance()
    {
        var repo = new FakeQuestionSamplesRepository(
        [
            new() { Id = 1, Environment = "General", GroupKey = "Concepts", GroupTitle = "Concepts", GroupOrder = 10, QuestionText = "What is an index?", QuestionOrder = 1 },
        ]);
        var service = CreateService(repo);

        var first = await service.GetAsync("General", false, CancellationToken.None);
        var second = await service.GetAsync("General", false, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, repo.CallCount);
    }

    [Fact]
    public async Task GetAsync_Refresh_Bypasses_Cache()
    {
        var repo = new FakeQuestionSamplesRepository(
        [
            new() { Id = 1, Environment = "General", GroupKey = "Concepts", GroupTitle = "Concepts", GroupOrder = 10, QuestionText = "What is an index?", QuestionOrder = 1 },
        ]);
        var service = CreateService(repo);

        await service.GetAsync("General", false, CancellationToken.None);
        await service.GetAsync("General", true, CancellationToken.None);

        Assert.Equal(2, repo.CallCount);
    }

    [Fact]
    public async Task GetAsync_Empty_Result_Returns_Empty_Groups()
    {
        var repo = new FakeQuestionSamplesRepository([]);
        var service = CreateService(repo);

        var response = await service.GetAsync("Windows_Live", false, CancellationToken.None);

        Assert.Equal("Windows_Live", response.Environment);
        Assert.Empty(response.Groups);
    }

    [Fact]
    public void BuildResponse_NullTags_Returns_Null_Tags_Array()
    {
        var rows = new List<QuestionSampleRow>
        {
            new() { Id = 1, Environment = "General", GroupKey = "G", GroupTitle = "G", GroupOrder = 1, QuestionText = "Q", QuestionOrder = 1, Tags = null },
        };

        var response = QuestionSamplesService.BuildResponse("General", rows);

        Assert.Null(response.Groups[0].Items[0].Tags);
    }

    [Fact]
    public void BuildResponse_CommaSeparatedTags_Parsed_Correctly()
    {
        var rows = new List<QuestionSampleRow>
        {
            new() { Id = 1, Environment = "General", GroupKey = "G", GroupTitle = "G", GroupOrder = 1, QuestionText = "Q", QuestionOrder = 1, Tags = " Backups , Size , Performance " },
        };

        var response = QuestionSamplesService.BuildResponse("General", rows);

        Assert.Equal(new[] { "Backups", "Size", "Performance" }, response.Groups[0].Items[0].Tags);
    }

    private static QuestionSamplesService CreateService(IQuestionSamplesRepository repo)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new QuestionSamplesService(repo, cache, NullLogger<QuestionSamplesService>.Instance);
    }

    private sealed class FakeQuestionSamplesRepository(List<QuestionSampleRow> rows) : IQuestionSamplesRepository
    {
        public int CallCount { get; private set; }

        public Task<List<QuestionSampleRow>> GetByEnvironmentAsync(
            string environment,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(rows.Where(r => r.Environment == environment).ToList());
        }

        public Task<QuestionSampleRow?> GetByIdAsync(
            int sampleId, string environment, CancellationToken cancellationToken)
            => Task.FromResult(rows.FirstOrDefault(r => r.Id == sampleId && r.Environment == environment));

        public Task<QuestionSampleRow?> GetByGroupKeyAsync(
            string groupKey, string environment, CancellationToken cancellationToken)
            => Task.FromResult(rows.FirstOrDefault(r => r.GroupKey == groupKey && r.Environment == environment));
    }
}
