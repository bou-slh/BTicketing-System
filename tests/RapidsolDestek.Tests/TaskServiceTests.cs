using Microsoft.EntityFrameworkCore;
using RapidsolDestek.Domain.Common;
using RapidsolDestek.Domain.Entities;
using RapidsolDestek.Infrastructure;
using RapidsolDestek.Infrastructure.Services;
using RapidsolDestek.Tests.Support;

namespace RapidsolDestek.Tests;

/// <summary>
/// S6 TaskService (agent/tasks.html + task-view.html): create / close / assign /
/// transfer / edit / delete with the task.* permission keys, thread events and the
/// task-sequence numbering. Assertions read through fresh scopes (EF identity-map
/// staleness across scopes).
/// </summary>
[Collection("Postgres")]
public class TaskServiceTests(PostgresFixture fixture)
{
    private static async Task<TaskItem> CreateTaskAsync(ServiceScopeBundle s, string? description = null)
    {
        // dkaya = Temsilci in Destek: holds task.create/edit/close there, not assign/transfer/delete.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        var destek = await s.Db.Departments.SingleAsync(d => d.Name == "Destek");
        return await s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
        {
            Title = $"Servis testi görevi {Guid.NewGuid():N}",
            DepartmentId = destek.Id,
            StaffId = dkaya.Id,
            DueDate = DateTimeOffset.UtcNow.AddDays(3),
            Description = description,
        }, dkaya);
    }

    private static async Task<List<string>> ThreadEventNamesAsync(AppDbContext db, int threadId) =>
        await db.ThreadEvents.Where(e => e.ThreadId == threadId)
            .Join(db.ThreadEventTypes, e => e.EventTypeId, t => t.Id, (e, t) => t.Name)
            .ToListAsync();

    [Fact]
    public async Task Create_DrawsSequenceNumber_OpensThread_WritesEvents()
    {
        int taskId, threadId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s, description: "Açıklama ilk kayıt olmalı.");
            taskId = task.Id;
            threadId = task.ThreadId;

            // Task numbering canon: settings tasks.number_format = "T-####".
            Assert.Matches("^T-[0-9]{4,}$", task.Number);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            var saved = await fresh.Db.TaskItems.SingleAsync(t => t.Id == taskId);
            Assert.Null(saved.ClosedAt);

            // Description became the first (non-note) entry of the task thread.
            var entry = await fresh.Db.ThreadEntries.SingleAsync(e => e.ThreadId == threadId);
            Assert.Equal(ThreadEntryType.Response, entry.Type);
            Assert.Equal("Açıklama ilk kayıt olmalı.", entry.Body);

            var names = await ThreadEventNamesAsync(fresh.Db, threadId);
            Assert.Contains("created", names);
            Assert.Contains("assigned", names);
        }
    }

    [Fact]
    public async Task Create_RequiresTaskCreatePermissionInTheDepartment()
    {
        using var s = new ServiceScopeBundle(fixture);
        // dkaya's only department is Destek → denied when creating into Bordro.
        var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
        var bordro = await s.Db.Departments.SingleAsync(d => d.Name == "Bordro");

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            s.Get<ITaskService>().CreateAsync(new TaskCreateRequest
            {
                Title = $"Yetkisiz görev {Guid.NewGuid():N}",
                DepartmentId = bordro.Id,
            }, dkaya));
    }

    [Fact]
    public async Task Close_StampsClosedAt_AppendsOptionalNote()
    {
        int taskId, threadId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s);
            taskId = task.Id;
            threadId = task.ThreadId;

            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await s.Get<ITaskService>().CloseAsync(taskId, "Kapanış notu.", dkaya);
            // Second close is a no-op, not an error.
            await s.Get<ITaskService>().CloseAsync(taskId, null, dkaya);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            var closed = await fresh.Db.TaskItems.SingleAsync(t => t.Id == taskId);
            Assert.NotNull(closed.ClosedAt);
            Assert.False(closed.IsOverdue);

            var note = await fresh.Db.ThreadEntries
                .SingleAsync(e => e.ThreadId == threadId && e.Type == ThreadEntryType.Note);
            Assert.Equal("Kapanış notu.", note.Body);
            Assert.Equal(1, (await ThreadEventNamesAsync(fresh.Db, threadId)).Count(n => n == "closed"));
        }
    }

    [Fact]
    public async Task Assign_RequiresPermission_WritesAssignedEvent()
    {
        int taskId, threadId, mcetinId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s);
            taskId = task.Id;
            threadId = task.ThreadId;

            // Temsilci holds no task.assign → denied even in the own department.
            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<ITaskService>().AssignAsync(taskId, dkaya.Id, null, dkaya));

            // uakin (Yönetici, admin) may assign anywhere.
            var uakin = await TestActors.StaffAsync(s.Db, "uakin");
            mcetinId = await s.Db.Staff.Where(x => x.Username == "mcetin").Select(x => x.Id).SingleAsync();
            await s.Get<ITaskService>().AssignAsync(taskId, mcetinId, null, uakin);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            var assigned = await fresh.Db.TaskItems.SingleAsync(t => t.Id == taskId);
            Assert.Equal(mcetinId, assigned.StaffId);
            Assert.Contains("assigned", await ThreadEventNamesAsync(fresh.Db, threadId));
        }
    }

    [Fact]
    public async Task Transfer_MovesDepartment_WritesTransferredEvent()
    {
        int taskId, threadId, danismanlikId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s);
            taskId = task.Id;
            threadId = task.ThreadId;
            danismanlikId = await s.Db.Departments
                .Where(d => d.Name == "Danışmanlık").Select(d => d.Id).SingleAsync();

            var uakin = await TestActors.StaffAsync(s.Db, "uakin");
            await s.Get<ITaskService>().TransferAsync(taskId, danismanlikId, uakin);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            var moved = await fresh.Db.TaskItems.SingleAsync(t => t.Id == taskId);
            Assert.Equal(danismanlikId, moved.DepartmentId);
            Assert.Contains("transferred", await ThreadEventNamesAsync(fresh.Db, threadId));
        }
    }

    [Fact]
    public async Task Update_ChangesTitleAndDue_WritesEditedEvent()
    {
        int taskId, threadId;
        var newDue = DateTimeOffset.UtcNow.AddDays(10);
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s);
            taskId = task.Id;
            threadId = task.ThreadId;

            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await s.Get<ITaskService>().UpdateAsync(taskId, "Yeni başlık", newDue, dkaya);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            var edited = await fresh.Db.TaskItems.SingleAsync(t => t.Id == taskId);
            Assert.Equal("Yeni başlık", edited.Title);
            Assert.NotNull(edited.DueDate);
            Assert.Contains("edited", await ThreadEventNamesAsync(fresh.Db, threadId));
        }
    }

    [Fact]
    public async Task Delete_RequiresTaskDelete_RemovesTaskAndThread()
    {
        int taskId, threadId;
        using (var s = new ServiceScopeBundle(fixture))
        {
            var task = await CreateTaskAsync(s, description: "Silinecek içerik.");
            taskId = task.Id;
            threadId = task.ThreadId;

            // Temsilci and Kıdemli Temsilci both lack task.delete.
            var dkaya = await TestActors.StaffAsync(s.Db, "dkaya");
            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                s.Get<ITaskService>().DeleteAsync(taskId, dkaya));

            var uakin = await TestActors.StaffAsync(s.Db, "uakin");
            await s.Get<ITaskService>().DeleteAsync(taskId, uakin);
        }

        using (var fresh = new ServiceScopeBundle(fixture))
        {
            Assert.False(await fresh.Db.TaskItems.AnyAsync(t => t.Id == taskId));
            Assert.False(await fresh.Db.Threads.AnyAsync(t => t.Id == threadId));
            Assert.False(await fresh.Db.ThreadEntries.AnyAsync(e => e.ThreadId == threadId));
            Assert.False(await fresh.Db.ThreadEvents.AnyAsync(e => e.ThreadId == threadId));
        }
    }
}
