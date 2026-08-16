using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Contracts;
using SubVid.Server.Controllers;
using SubVid.Server.Data;
using SubVid.Server.Models;

namespace SubVid.App.Tests;

[Collection("SQL Server integration")]
public sealed class ProjectApiIntegrationTests
{
    private const string ConnectionString =
        "Data Source=DUNGDEV;Initial Catalog=TOOL_VIETSUB;Integrated Security=True;Trust Server Certificate=True";

    [Fact]
    public async Task List_ExecutesOwnerScopedProjectionOnSqlServer()
    {
        var projectId = Guid.NewGuid();
        try
        {
            await using var database = CreateDatabase();
            var user = await database.Users.AsNoTracking().SingleAsync(
                item => item.EmailNormalized == "ADMIN@TOOLVIETSUB.LOCAL");
            var nowUtc = DateTime.UtcNow;
            database.Projects.Add(new Project
            {
                ProjectId = projectId,
                OwnerUserId = user.UserId,
                ProjectName = "__PROJECT_API_INTEGRATION_TEST__",
                StatusCode = "DRAFT",
                TargetLanguageCode = "vi",
                CurrentTranscriptVersion = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
            await database.SaveChangesAsync();

            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(JwtRegisteredClaimNames.Sub, user.UserId.ToString())],
                    "integration-test")),
            };
            var controller = new ProjectsController(database)
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };

            var action = await controller.List(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(action);
            var envelope = Assert.IsType<ApiEnvelope<IReadOnlyList<ProjectResponse>>>(ok.Value);
            Assert.True(envelope.Success);
            Assert.Contains(envelope.Data!, item =>
                item.ProjectId == projectId && item.Name == "__PROJECT_API_INTEGRATION_TEST__");
        }
        finally
        {
            await using var cleanup = CreateDatabase();
            await cleanup.Projects
                .Where(item => item.ProjectId == projectId)
                .ExecuteDeleteAsync();
        }
    }

    private static SubVidDbContext CreateDatabase()
    {
        var options = new DbContextOptionsBuilder<SubVidDbContext>()
            .UseSqlServer(ConnectionString)
            .EnableDetailedErrors()
            .Options;
        return new SubVidDbContext(options);
    }
}
