// Unit tests for OrchestratorOrderParsing covering analytical functions from sheet 46e.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class OrchestratorOrderParsingTests
{
    // --- 2.1 LooksLikeQuestion Tests ---

    [Theory]
    [InlineData("Is the build passing?")]
    [InlineData("Can we discuss the plan?")]
    [InlineData("How far did we get?")]
    [InlineData("status")]
    [InlineData("quota")]
    [InlineData("how far along are we")]
    [InlineData("what is the lead model")]
    [InlineData("why did this fail")]
    [InlineData("who is running")]
    [InlineData("where is the repo")]
    [InlineData("when does it finish")]
    [InlineData("which worker is free")]
    [InlineData("come sta il worker")]
    [InlineData("come mai fallisce")]
    [InlineData("che ne dici")]
    [InlineData("che sta facendo")]
    [InlineData("che fa il lead")]
    [InlineData("chi sta lavorando")]
    [InlineData("perché fallisce")]
    [InlineData("perche fallisce")]
    [InlineData("quanto manca")]
    [InlineData("quale modello è attivo")]
    public void LooksLikeQuestion_PositiveCases(string input)
    {
        Assert.True(OrchestratorOrderParsing.LooksLikeQuestion(input));
    }

    [Theory]
    [InlineData("Proceed with implementation")]
    [InlineData("Fallo subito")]
    [InlineData("Start the job")]
    [InlineData("Turn off Luna")]
    [InlineData("implement the auth parser")]
    [InlineData("smista i worker")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LooksLikeQuestion_NegativeCases(string? input)
    {
        Assert.False(OrchestratorOrderParsing.LooksLikeQuestion(input));
    }

    // --- 2.2 LooksLikeWork Tests ---

    [Theory]
    [InlineData("do it")]
    [InlineData("fallo")]
    [InlineData("procedi")]
    [InlineData("smista")]
    [InlineData("go")]
    [InlineData("DO IT")]
    [InlineData("  fallo  ")]
    public void LooksLikeWork_ExactWorkTriggers(string input)
    {
        Assert.True(OrchestratorOrderParsing.LooksLikeWork(input));
    }

    [Theory]
    [InlineData("implement the auth parser")]
    [InlineData("implementa il sistema")]
    [InlineData("implementare la pipeline")]
    [InlineData("dispatch workers now")]
    [InlineData("assign worker alpha")]
    [InlineData("assigna worker beta")]
    [InlineData("metti i worker sul job")]
    [InlineData("dai il lavoro a grok")]
    [InlineData("do the job")]
    [InlineData("do the task")]
    [InlineData("do the work")]
    [InlineData("fai il job")]
    [InlineData("fai la task")]
    [InlineData("fai il lavoro")]
    [InlineData("fallo fare a luna")]
    [InlineData("lavora su questo bug")]
    [InlineData("lavoraci")]
    [InlineData("procedi con la build")]
    [InlineData("esegui il comando")]
    [InlineData("costruisci il modulo")]
    [InlineData("continua il lavoro")]
    [InlineData("continuare la compilazione")]
    [InlineData("continue the job")]
    [InlineData("go ahead with deployment")]
    [InlineData("vai avanti con la build")]
    [InlineData("fix the failing test")]
    [InlineData("fix this error")]
    [InlineData("sistema il bug")]
    public void LooksLikeWork_BoundaryWorkKeywords(string input)
    {
        Assert.True(OrchestratorOrderParsing.LooksLikeWork(input));
    }

    [Theory]
    [InlineData("What is the current status?")]
    [InlineData("How far did we get?")]
    [InlineData("Tell me about the architecture")]
    [InlineData("Can we discuss the plan?")]
    [InlineData("Do not resume Fixture alpha")]
    [InlineData("The start button should resume work.")]
    [InlineData("Stop should only affect one worker")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LooksLikeWork_NegativeCases(string? input)
    {
        Assert.False(OrchestratorOrderParsing.LooksLikeWork(input));
    }

    // --- 2.3 FreshTaskIntent Tests ---

    [Theory]
    [InlineData("implement the parser")]
    [InlineData("implementa il controller")]
    [InlineData("implementare i test")]
    [InlineData("build the backend engine")]
    [InlineData("fix the failing test")]
    [InlineData("sistema il modulo")]
    [InlineData("esegui la migrazione")]
    [InlineData("fai un refactor")]
    [InlineData("audit security settings")]
    [InlineData("review PR 244")]
    [InlineData("analizza i log")]
    [InlineData("controlla il database")]
    [InlineData("verifica la build")]
    [InlineData("please implement the parser")]
    [InlineData("per favore fai il refactoring")]
    [InlineData("vorrei implementare un endpoint")]
    [InlineData("voglio fare audit")]
    [InlineData("puoi implementare la libreria")]
    [InlineData("can you fix the login issue")]
    [InlineData("can you review the PR")]
    [InlineData("can you implement a parser")]
    [InlineData("can you an audit")]
    public void FreshTaskIntent_PositiveCases(string input)
    {
        Assert.True(OrchestratorOrderParsing.FreshTaskIntent(input));
    }

    [Theory]
    [InlineData("what is the status?")]
    [InlineData("hello")]
    [InlineData("thanks")]
    [InlineData("done")]
    [InlineData("The start button should resume work.")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void FreshTaskIntent_NegativeCases(string? input)
    {
        Assert.False(OrchestratorOrderParsing.FreshTaskIntent(input));
    }

    // --- 2.4 CheckpointTalk Tests ---

    [Fact]
    public void CheckpointTalk_ApplicationUpdate()
    {
        var info = OrchestratorOrderParsing.CheckpointTalk("application update");
        Assert.Equal("Paused for an application update. Checkpoint saved.", info.Activity);
        Assert.Equal("Paused for an application update. Checkpoint saved. Press Resume after restart.", info.Note);
        Assert.Equal("Saved unfinished work before updating.", info.Orch);
        Assert.Equal("Paused Demo: Fix Auth. Update checkpoint saved.", info.FormatDoing("Demo", "Fix Auth"));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("shutdown")]
    public void CheckpointTalk_StopOrShutdown(string reason)
    {
        var info = OrchestratorOrderParsing.CheckpointTalk(reason);
        Assert.Equal("Stopped. Checkpoint saved.", info.Activity);
        Assert.Equal("Stopped. Checkpoint saved. Press Resume to continue.", info.Note);
        Assert.Equal("Stopped. Checkpoint saved.", info.Orch);
        Assert.Equal("Stopped Demo · Fix Auth. Checkpoint saved.", info.FormatDoing("Demo", "Fix Auth"));
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("")]
    [InlineData(null)]
    public void CheckpointTalk_DeadlineOrEmpty(string? reason)
    {
        var info = OrchestratorOrderParsing.CheckpointTalk(reason);
        Assert.Equal("Deadline reached. Checkpoint saved.", info.Activity);
        Assert.Equal("Deadline reached. Checkpoint saved. Resume next session.", info.Note);
        Assert.Equal("Paused at deadline. Checkpoint saved.", info.Orch);
        Assert.Equal("Deadline reached on Demo · Fix Auth. Checkpoint saved.", info.FormatDoing("Demo", "Fix Auth"));
    }

    [Fact]
    public void CheckpointTalk_CustomReason()
    {
        var info = OrchestratorOrderParsing.CheckpointTalk("disk space full");
        Assert.Equal("Job ended without completing. Checkpoint saved.", info.Activity);
        Assert.Equal("Job ended (disk space full). Checkpoint saved. Press Resume to continue.", info.Note);
        Assert.Equal("Job ended without completing. Checkpoint saved.", info.Orch);
        Assert.Equal("Demo · Fix Auth ended without completing (disk space full). Checkpoint saved.", info.FormatDoing("Demo", "Fix Auth"));
    }

    // --- 2.5 ResearchIntent Tests ---

    [Theory]
    [InlineData("research parser options")]
    [InlineData("investigate test failures")]
    [InlineData("cerca documentazione")]
    [InlineData("ricerca algoritmi di compressione")]
    [InlineData("indaga sul timeout")]
    public void ResearchIntent_LeadingVerbs(string input)
    {
        Assert.True(OrchestratorOrderParsing.ResearchIntent(input));
    }

    [Theory]
    [InlineData("fai ricerca sui log di sistema")]
    [InlineData("fa ricerca sui moduli")]
    [InlineData("manda una ricerca approfondita")]
    [InlineData("assegna ricerca architetturale")]
    [InlineData("chiedi di cercare le specifiche")]
    [InlineData("vorrei una ricerca sulle alternative")]
    [InlineData("voglio fare ricerche")]
    [InlineData("puoi cercare la causa")]
    [InlineData("can you research the api")]
    [InlineData("please investigate memory leaks")]
    [InlineData("have someone research this")]
    [InlineData("ask him to investigate")]
    [InlineData("assign research to worker")]
    [InlineData("delegate researching to agent")]
    [InlineData("send research request")]
    [InlineData("avvia ricerche sui token")]
    public void ResearchIntent_DelegationPhrases(string input)
    {
        Assert.True(OrchestratorOrderParsing.ResearchIntent(input));
    }

    [Theory]
    [InlineData("do it")]
    [InlineData("fallo")]
    [InlineData("procedi")]
    [InlineData("vai")]
    [InlineData("go ahead")]
    [InlineData("yes")]
    [InlineData("sì")]
    [InlineData("si")]
    public void ResearchIntent_MultiTurnAffirmativeWithResearchHistory(string input)
    {
        var historyWithQuestion = new[]
        {
            new TurnHistory("Should we research alternative parsers?", "Yes, we could check AST options.")
        };
        Assert.True(OrchestratorOrderParsing.ResearchIntent(input, historyWithQuestion));

        var historyWithAnswer = new[]
        {
            new TurnHistory("What should we do?", "I can conduct a ricerca on performance.")
        };
        Assert.True(OrchestratorOrderParsing.ResearchIntent(input, historyWithAnswer));
    }

    [Fact]
    public void ResearchIntent_MultiTurnNegativeCases()
    {
        // Affirmative token but history does not mention research
        var nonResearchHistory = new[]
        {
            new TurnHistory("Build completed cleanly.", "Ready for next step.")
        };
        Assert.False(OrchestratorOrderParsing.ResearchIntent("do it", nonResearchHistory));

        // Research history present but user input is not affirmative
        var researchHistory = new[]
        {
            new TurnHistory("Should we research options?", "Yes.")
        };
        Assert.False(OrchestratorOrderParsing.ResearchIntent("sounds good", researchHistory));
        Assert.False(OrchestratorOrderParsing.ResearchIntent("maybe later", researchHistory));

        // Null and empty inputs
        Assert.False(OrchestratorOrderParsing.ResearchIntent(null));
        Assert.False(OrchestratorOrderParsing.ResearchIntent(""));
        Assert.False(OrchestratorOrderParsing.ResearchIntent("   "));
        Assert.False(OrchestratorOrderParsing.ResearchIntent("implement the auth parser"));
    }

    // --- 2.6 PickAskedProject Tests ---

    [Fact]
    public void PickAskedProject_ExplicitId_MatchesDirectly()
    {
        var p1 = new ProjectSummary("p1", "Alpha Project");
        var p2 = new ProjectSummary("p2", "Beta Project");
        var snap = new OrchestratorSnapshot(new[] { p1, p2 });

        var match = OrchestratorOrderParsing.PickAskedProject(snap, "p2", "How is Alpha doing?");
        Assert.NotNull(match);
        Assert.Equal("p2", match.Id);
        Assert.Equal("Beta Project", match.Name);

        var notFound = OrchestratorOrderParsing.PickAskedProject(snap, "nonexistent", "Hello");
        Assert.Null(notFound);
    }

    [Fact]
    public void PickAskedProject_MatchesFromQuestionText()
    {
        var p1 = new ProjectSummary("proj-alpha", "Alpha Service");
        var p2 = new ProjectSummary("proj-beta", "Beta Service");
        var snap = new OrchestratorSnapshot(new[] { p1, p2 });

        // Match by Name
        var byName = OrchestratorOrderParsing.PickAskedProject(snap, null, "Stop the Alpha Service now");
        Assert.NotNull(byName);
        Assert.Equal("proj-alpha", byName.Id);

        // Match by Id
        var byId = OrchestratorOrderParsing.PickAskedProject(snap, null, "Status of proj-beta please");
        Assert.NotNull(byId);
        Assert.Equal("proj-beta", byId.Id);
    }

    [Fact]
    public void PickAskedProject_SingleProjectFallback_AndMultipleProjectAmbiguity()
    {
        var p1 = new ProjectSummary("p1", "Single Project");
        var singleSnap = new OrchestratorSnapshot(new[] { p1 });

        // Single project defaults when no project name in question
        var single = OrchestratorOrderParsing.PickAskedProject(singleSnap, null, "What is the status?");
        Assert.NotNull(single);
        Assert.Equal("p1", single.Id);

        // Multiple projects return null when question doesn't specify
        var p2 = new ProjectSummary("p2", "Second Project");
        var multiSnap = new OrchestratorSnapshot(new[] { p1, p2 });
        var multi = OrchestratorOrderParsing.PickAskedProject(multiSnap, null, "What is the status?");
        Assert.Null(multi);

        // Empty snapshot returns null
        var emptySnap = new OrchestratorSnapshot(Array.Empty<ProjectSummary>());
        Assert.Null(OrchestratorOrderParsing.PickAskedProject(emptySnap, null, "test"));
        Assert.Null(OrchestratorOrderParsing.PickAskedProject((OrchestratorSnapshot?)null, null, "test"));
    }

    // --- 2.7 ParseOrder Tests ---

    [Fact]
    public void ParseOrder_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(OrchestratorOrderParsing.ParseOrder(null, (OrchestratorSnapshot?)null, null));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("", (OrchestratorSnapshot?)null, null));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("   ", (OrchestratorSnapshot?)null, null));
    }

    [Fact]
    public void ParseOrder_ModelToggles_AndLead()
    {
        var models = new[]
        {
            new ModelCatalogEntry("luna", "Luna"),
            new ModelCatalogEntry("grok", "Grok")
        };
        var snap = new OrchestratorSnapshot(Models: models);

        // Turn off
        var offOrder = OrchestratorOrderParsing.ParseOrder("Turn off Luna", snap, null);
        Assert.NotNull(offOrder);
        var toggleOff = Assert.IsType<ModelToggleOrder>(offOrder);
        Assert.Equal("luna", toggleOff.Id);
        Assert.False(toggleOff.Enabled);
        Assert.Equal("Luna", toggleOff.Label);
        var offJson = toggleOff.ToJsonObject();
        Assert.Equal("model", offJson["action"]?.GetValue<string>());
        Assert.False(offJson["enabled"]?.GetValue<bool>());

        // Turn on
        var onOrder = OrchestratorOrderParsing.ParseOrder("Turn on luna", snap, null);
        Assert.NotNull(onOrder);
        var toggleOn = Assert.IsType<ModelToggleOrder>(onOrder);
        Assert.Equal("luna", toggleOn.Id);
        Assert.True(toggleOn.Enabled);

        // Italian toggles
        var spegni = OrchestratorOrderParsing.ParseOrder("spegni Luna!", snap, null);
        Assert.NotNull(spegni);
        Assert.False(Assert.IsType<ModelToggleOrder>(spegni).Enabled);

        var attiva = OrchestratorOrderParsing.ParseOrder("attiva Luna.", snap, null);
        Assert.NotNull(attiva);
        Assert.True(Assert.IsType<ModelToggleOrder>(attiva).Enabled);

        // Lead selection
        var leadOrder = OrchestratorOrderParsing.ParseOrder("Use Grok as orchestrator", snap, null);
        Assert.NotNull(leadOrder);
        var lead = Assert.IsType<LeadSelectOrder>(leadOrder);
        Assert.Equal("grok", lead.Id);
        Assert.Equal("Grok", lead.Label);
        var leadJson = lead.ToJsonObject();
        Assert.Equal("lead", leadJson["action"]?.GetValue<string>());
        Assert.Equal("grok", leadJson["id"]?.GetValue<string>());

        // Mentioning model without toggle or lead returns null
        Assert.Null(OrchestratorOrderParsing.ParseOrder("What is Luna doing?", snap, null));
    }

    [Fact]
    public void ParseOrder_LifecycleCommands()
    {
        var proj = new ProjectSummary("demo", "Demo");
        var snap = new OrchestratorSnapshot(Projects: new[] { proj });

        // Named lifecycle orders
        var stopOrder = OrchestratorOrderParsing.ParseOrder("Stop Demo", snap, null);
        Assert.NotNull(stopOrder);
        var stop = Assert.IsType<LifecycleOrder>(stopOrder);
        Assert.Equal("stop", stop.Action);
        Assert.Equal("demo", stop.ProjectId);
        Assert.Equal("Demo", stop.Name);

        var resumeOrder = OrchestratorOrderParsing.ParseOrder("Resume Demo", snap, null);
        Assert.NotNull(resumeOrder);
        Assert.Equal("resume", Assert.IsType<LifecycleOrder>(resumeOrder).Action);

        var approveOrder = OrchestratorOrderParsing.ParseOrder("Approve Demo", snap, null);
        Assert.NotNull(approveOrder);
        Assert.Equal("approve", Assert.IsType<LifecycleOrder>(approveOrder).Action);

        var startOrder = OrchestratorOrderParsing.ParseOrder("Start Demo", snap, null);
        Assert.NotNull(startOrder);
        Assert.Equal("start", Assert.IsType<LifecycleOrder>(startOrder).Action);

        // Italian lifecycle commands with single project implicit recipient
        var riprendi = OrchestratorOrderParsing.ParseOrder("riprendi", snap, "demo");
        Assert.NotNull(riprendi);
        Assert.Equal("resume", Assert.IsType<LifecycleOrder>(riprendi).Action);

        var approva = OrchestratorOrderParsing.ParseOrder("approva", snap, "demo");
        Assert.NotNull(approva);
        Assert.Equal("approve", Assert.IsType<LifecycleOrder>(approva).Action);

        var avvia = OrchestratorOrderParsing.ParseOrder("avvia", snap, "demo");
        Assert.NotNull(avvia);
        Assert.Equal("start", Assert.IsType<LifecycleOrder>(avvia).Action);

        var ferma = OrchestratorOrderParsing.ParseOrder("ferma", snap, "demo");
        Assert.NotNull(ferma);
        Assert.Equal("stop", Assert.IsType<LifecycleOrder>(ferma).Action);

        // Non-operational conversational phrases containing lifecycle words return null
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Do not resume Fixture alpha", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("The start button should resume work.", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Stop should only affect one worker", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Implement the parser on Demo", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Smista i worker su Demo", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Continue Demo", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Skip grill on Demo", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Grill off", snap, "demo"));
    }

    [Theory]
    [InlineData("mode normal", "normal")]
    [InlineData("modalità normale", "normal")]
    [InlineData("modalita normale", "normal")]
    [InlineData("mode safe", "safe")]
    [InlineData("modalità sicura", "safe")]
    [InlineData("modalita sicura", "safe")]
    [InlineData("mode ultra safe", "ultra-safe")]
    [InlineData("mode ultra-safe", "ultra-safe")]
    [InlineData("modalità ultra sicura", "ultra-safe")]
    [InlineData("modalita ultra sicura", "ultra-safe")]
    public void ParseOrder_SavingModes(string input, string expectedMode)
    {
        var proj = new ProjectSummary("p1", "Alpha");
        var snap = new OrchestratorSnapshot(Projects: new[] { proj });

        var order = OrchestratorOrderParsing.ParseOrder(input, snap, "p1");
        Assert.NotNull(order);
        var saving = Assert.IsType<SavingModeOrder>(order);
        Assert.Equal("saving-mode", saving.Action);
        Assert.Equal(expectedMode, saving.SavingMode);
        Assert.Equal("p1", saving.ProjectId);
        var json = saving.ToJsonObject();
        Assert.Equal("saving-mode", json["action"]?.GetValue<string>());
        Assert.Equal(expectedMode, json["savingMode"]?.GetValue<string>());
    }

    [Fact]
    public void ParseOrder_ChatModes_AndRejections()
    {
        var snap = new OrchestratorSnapshot();

        // Direct chat modes
        var talk = OrchestratorOrderParsing.ParseOrder("mode talk", snap, "p1");
        Assert.NotNull(talk);
        var talkOrder = Assert.IsType<ChatModeOrder>(talk);
        Assert.Equal("mode", talkOrder.Action);
        Assert.Equal("talk", talkOrder.Mode);
        Assert.Equal("talk", talkOrder.ChatMode);

        var plan = OrchestratorOrderParsing.ParseOrder("mode plan", snap, "p1");
        Assert.NotNull(plan);
        var planOrder = Assert.IsType<ChatModeOrder>(plan);
        Assert.Equal("plan", planOrder.Mode);
        Assert.Equal("plan", planOrder.ChatMode);

        // Rejected pipeline mode
        var pipeline = OrchestratorOrderParsing.ParseOrder("mode pipeline", snap, "p1");
        Assert.NotNull(pipeline);
        var reject = Assert.IsType<RejectOrder>(pipeline);
        Assert.Equal("reject", reject.Action);
        Assert.Equal("pipeline", reject.Mode);

        // Contextual switches
        var autoSwitch = OrchestratorOrderParsing.ParseOrder("Switch to automatic", snap, null);
        Assert.NotNull(autoSwitch);
        var auto = Assert.IsType<ChatModeOrder>(autoSwitch);
        Assert.Equal("auto", auto.Mode);
        Assert.Null(auto.ChatMode);

        var manualSwitch = OrchestratorOrderParsing.ParseOrder("Switch to manual", snap, null);
        Assert.NotNull(manualSwitch);
        var manual = Assert.IsType<ChatModeOrder>(manualSwitch);
        Assert.Equal("manual", manual.Mode);
        Assert.Equal("plan", manual.ChatMode);

        var rejectedContext = OrchestratorOrderParsing.ParseOrder("passa a pipeline", snap, null);
        Assert.NotNull(rejectedContext);
        Assert.IsType<RejectOrder>(rejectedContext);
    }

    // --- 2.8 RouteAsk Tests ---

    [Fact]
    public void RouteAsk_DeletedOrMissingProjectId_FailsWithReason()
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });
        var res = OrchestratorOrderParsing.RouteAsk("status", snap, "deleted");
        Assert.False(res.Ok);
        Assert.Equal("Selected project no longer exists. Select a project before sending.", res.Reason);
    }

    [Fact]
    public void RouteAsk_CrossProjectMismatch_FailsWithReason()
    {
        var snap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });
        var res = OrchestratorOrderParsing.RouteAsk("Stop Beta now", snap, "p1");
        Assert.False(res.Ok);
        Assert.Equal("Message names a different project from the selected recipient. Select the intended project before sending.", res.Reason);
    }

    [Fact]
    public void RouteAsk_MultiProjectAmbiguity_FailsWithReason()
    {
        var snap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });
        var res = OrchestratorOrderParsing.RouteAsk("Should we copy Alpha to Beta?", snap, null);
        Assert.False(res.Ok);
        Assert.Equal("Message names multiple projects. Select the intended recipient.", res.Reason);
    }

    [Theory]
    [InlineData("grill me", "me")]
    [InlineData("grill with docs", "docs")]
    [InlineData("grill with the docs", "docs")]
    public void RouteAsk_GrillCommands_RequireProject(string question, string expectedKind)
    {
        var multiSnap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });

        // Fails when project cannot be resolved
        var fail = OrchestratorOrderParsing.RouteAsk(question, multiSnap, null);
        Assert.False(fail.Ok);
        Assert.Equal("Select the project before starting Grill.", fail.Reason);

        // Succeeds when project is provided
        var success = OrchestratorOrderParsing.RouteAsk(question, multiSnap, "p1");
        Assert.True(success.Ok);
        Assert.Equal(expectedKind, success.GrillKind);
        Assert.Null(success.Order);
    }

    [Fact]
    public void RouteAsk_GrillTurnContinuation_AndEndGrill()
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });
        var history = new[]
        {
            new TurnHistory("What are the requirements?", "We need auth.", ProjectId: "p1", GrillKind: "me")
        };

        // Normal chat turn during active grill maintains grillKind
        var continuing = OrchestratorOrderParsing.RouteAsk("Let's use OAuth2.", snap, "p1", history);
        Assert.True(continuing.Ok);
        Assert.Equal("me", continuing.GrillKind);

        // End grill commands produce endGrill system query order
        var end = OrchestratorOrderParsing.RouteAsk("stop grill", snap, "p1", history);
        Assert.True(end.Ok);
        Assert.NotNull(end.Order);
        var endOrder = Assert.IsType<SystemQueryOrder>(end.Order);
        Assert.Equal("endGrill", endOrder.Action);
    }

    [Fact]
    public void RouteAsk_ResearchIntent_RequiresProject()
    {
        var multiSnap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });

        var fail = OrchestratorOrderParsing.RouteAsk("research parser options", multiSnap, null);
        Assert.False(fail.Ok);
        Assert.Equal("Select a project or name it explicitly before dispatching research.", fail.Reason);

        var success = OrchestratorOrderParsing.RouteAsk("research parser options", multiSnap, "p1");
        Assert.True(success.Ok);
        Assert.True(success.ResearchRequested);
        Assert.Null(success.Order);
    }

    [Theory]
    [InlineData("quota", "quota")]
    [InlineData("quota remaining", "quota")]
    [InlineData("remaining quota", "quota")]
    [InlineData("how much quota is left?", "quota")]
    [InlineData("provider status", "quota")]
    [InlineData("model status", "quota")]
    [InlineData("status", "state")]
    [InlineData("current state", "state")]
    [InlineData("current process state", "state")]
    [InlineData("current checkpoint", "state")]
    [InlineData("current checkpoint identity", "state")]
    [InlineData("selected project", "state")]
    public void RouteAsk_SystemQueries(string question, string expectedAction)
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });
        var res = OrchestratorOrderParsing.RouteAsk(question, snap, "p1");
        Assert.True(res.Ok);
        Assert.NotNull(res.Order);
        var query = Assert.IsType<SystemQueryOrder>(res.Order);
        Assert.Equal(expectedAction, query.Action);
    }

    [Fact]
    public void RouteAsk_LifecycleOrders_RequireProject()
    {
        var multiSnap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });

        // Lifecycle command without project recipient fails
        var fail = OrchestratorOrderParsing.RouteAsk("stop", multiSnap, null);
        Assert.False(fail.Ok);
        Assert.Equal("Select a project or name it explicitly before issuing a command.", fail.Reason);

        // With recipient succeeds
        var ok = OrchestratorOrderParsing.RouteAsk("stop", multiSnap, "p1");
        Assert.True(ok.Ok);
        Assert.NotNull(ok.Order);
        Assert.Equal("stop", ok.Order.Action);
    }

    [Fact]
    public void RouteAsk_GoldModelConstraints()
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });

        // Gold model cannot be applied to resume/approve
        var failResume = OrchestratorOrderParsing.RouteAsk(
            "resume", snap, "p1", null, goldModel: "gemini-pro");
        Assert.False(failResume.Ok);
        Assert.Equal("Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.", failResume.Reason);

        var failApprove = OrchestratorOrderParsing.RouteAsk(
            "approve", snap, "p1", null, goldModel: "gemini-pro");
        Assert.False(failApprove.Ok);
        Assert.Equal("Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.", failApprove.Reason);

        // Gold model applies to start
        var startOk = OrchestratorOrderParsing.RouteAsk(
            "start", snap, "p1", null, goldModel: "gemini-pro");
        Assert.True(startOk.Ok);
        Assert.NotNull(startOk.Order);
        Assert.Equal("gemini-pro", startOk.Order.GoldModel);
    }

    [Fact]
    public void RouteAsk_FreshTaskIntent_RequiresProjectAndRespectsTalkMode()
    {
        var multiSnap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha", ChatMode: "plan"),
            new ProjectSummary("p2", "Beta", ChatMode: "plan")
        });

        // Missing project for fresh task fails
        var fail = OrchestratorOrderParsing.RouteAsk("implement the auth parser", multiSnap, null);
        Assert.False(fail.Ok);
        Assert.Equal("Select the project for this new task.", fail.Reason);

        // With project, triggers FreshTask
        var success = OrchestratorOrderParsing.RouteAsk("implement the auth parser", multiSnap, "p1");
        Assert.True(success.Ok);
        Assert.True(success.FreshTask);
        Assert.NotNull(success.TargetProject);
        Assert.Equal("p1", success.TargetProject.Id);

        // When project chatMode is 'talk', freshTask is not triggered
        var talkSnap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha", ChatMode: "talk")
        });
        var talkRes = OrchestratorOrderParsing.RouteAsk("implement the auth parser", talkSnap, "p1");
        Assert.True(talkRes.Ok);
        Assert.False(talkRes.FreshTask);
    }

    [Fact]
    public void RouteAsk_JsonNodeOverloads()
    {
        var snapNode = new JsonObject
        {
            ["projects"] = new JsonArray
            {
                new JsonObject { ["id"] = "p1", ["name"] = "Alpha", ["chatMode"] = "plan" }
            },
            ["models"] = new JsonArray
            {
                new JsonObject { ["id"] = "luna", ["label"] = "Luna" }
            }
        };

        var routeRes = OrchestratorOrderParsing.RouteAsk("status", snapNode, "p1");
        Assert.True(routeRes.Ok);
        Assert.Equal("state", routeRes.Order?.Action);

        var parsed = OrchestratorOrderParsing.ParseOrder("Turn off Luna", snapNode, "p1");
        Assert.NotNull(parsed);
        Assert.Equal("model", parsed.Action);

        var project = OrchestratorOrderParsing.PickAskedProject(snapNode, "p1", "test");
        Assert.NotNull(project);
        Assert.Equal("Alpha", project.Name);
    }

    // --- Part 1: Edge Cases for Heuristics and CheckpointTalk ---

    [Theory]
    [InlineData("   how does it work")]
    [InlineData("HOW does this happen")]
    [InlineData("sTaTuS")]
    [InlineData("qUoTa")]
    [InlineData("is it done???")]
    [InlineData("what is this?")]
    [InlineData("?")]
    [InlineData("???")]
    [InlineData("quanto manca?")]
    [InlineData("perché non funziona")]
    [InlineData("perche non compila")]
    [InlineData("chi sta eseguendo")]
    [InlineData("perchance")] // Matches ^perch by prefix design in sheet 46e
    public void LooksLikeQuestion_EdgeCases_Positive(string input)
    {
        Assert.True(OrchestratorOrderParsing.LooksLikeQuestion(input));
    }

    [Theory]
    [InlineData("somewhat strange")]
    [InlineData("statusquo")]
    [InlineData("quotas")]
    [InlineData("whoever")]
    [InlineData("somewhere")]
    [InlineData("notnow")]
    [InlineData("what?!")] // Doesn't end with ? and 'what' lacks required following space
    public void LooksLikeQuestion_EdgeCases_EmbeddedWords_Negative(string input)
    {
        Assert.False(OrchestratorOrderParsing.LooksLikeQuestion(input));
    }

    [Theory]
    [InlineData("FALLO")]
    [InlineData("SMISTA")]
    [InlineData("GO")]
    [InlineData("PROCEDI")]
    [InlineData("DO IT")]
    [InlineData("vai avanti!")]
    [InlineData("fai il lavoro.")]
    [InlineData("sistema il bug!")]
    public void LooksLikeWork_EdgeCases_Positive(string input)
    {
        Assert.True(OrchestratorOrderParsing.LooksLikeWork(input));
    }

    [Theory]
    [InlineData("going")]
    [InlineData("good")]
    [InlineData("gold")]
    [InlineData("goldModel")]
    [InlineData("gopher")]
    public void LooksLikeWork_EdgeCases_Negative(string input)
    {
        Assert.False(OrchestratorOrderParsing.LooksLikeWork(input));
    }

    [Theory]
    [InlineData("per favore verifica i file")]
    [InlineData("vorrei fare audit")]
    [InlineData("voglio implementare la feature")]
    [InlineData("puoi verifica il codice")]
    [InlineData("puoi fare controlla")]
    [InlineData("PLEASE FIX THE BUG")]
    [InlineData("CAN YOU AUDIT THIS")]
    public void FreshTaskIntent_EdgeCases_Positive(string input)
    {
        Assert.True(OrchestratorOrderParsing.FreshTaskIntent(input));
    }

    [Theory]
    [InlineData("please explain the concept")]
    [InlineData("can you tell me a story")]
    [InlineData("how to implement this")]
    [InlineData("vorrei capire il codice")]
    [InlineData("puoi spiegare l'architettura")]
    [InlineData("vorrei fare un audit")] // 'fare un' is not in regex (matches either fare or an? but not both)
    [InlineData("puoi fare una verifica")]
    [InlineData("puoi verificare il codice")] // Regex specifies 'verifica' rather than infinitive 'verificare'
    public void FreshTaskIntent_EdgeCases_Negative(string input)
    {
        Assert.False(OrchestratorOrderParsing.FreshTaskIntent(input));
    }

    [Fact]
    public void ResearchIntent_MultiTurn_OnlyImmediatePreviousTurnChecked()
    {
        // Turn 0 is non-research, Turn 1 is research: affirmative confirmation must return false
        var history = new[]
        {
            new TurnHistory("Build completed cleanly.", "Ready for next step."),
            new TurnHistory("Should we research options?", "Yes, we can investigate.")
        };
        Assert.False(OrchestratorOrderParsing.ResearchIntent("yes", history));
        Assert.False(OrchestratorOrderParsing.ResearchIntent("fallo", history));

        // Turn 0 with null strings should not throw and return false
        var nullHistory = new[]
        {
            new TurnHistory(null!, null!)
        };
        Assert.False(OrchestratorOrderParsing.ResearchIntent("yes", nullHistory));

        // Affirmative with punctuation
        var researchHistory = new[]
        {
            new TurnHistory("Should we investigate?", "Yes.")
        };
        Assert.True(OrchestratorOrderParsing.ResearchIntent("yes!", researchHistory));
        Assert.True(OrchestratorOrderParsing.ResearchIntent("fallo.", researchHistory));
        Assert.True(OrchestratorOrderParsing.ResearchIntent("procedi!", researchHistory));
        Assert.True(OrchestratorOrderParsing.ResearchIntent("sì!", researchHistory));
        Assert.True(OrchestratorOrderParsing.ResearchIntent("go ahead.", researchHistory));
    }

    [Fact]
    public void CheckpointTalk_SpecialCharacters_AndFormatting()
    {
        var info = OrchestratorOrderParsing.CheckpointTalk("worker process exited with code 137 (SIGKILL)");
        Assert.Equal("Job ended without completing. Checkpoint saved.", info.Activity);
        Assert.Equal("Job ended (worker process exited with code 137 (SIGKILL)). Checkpoint saved. Press Resume to continue.", info.Note);
        Assert.Equal("Job ended without completing. Checkpoint saved.", info.Orch);

        var doing = info.FormatDoing("Project [A/B]", "Fix {Bug} #123");
        Assert.Equal("Project [A/B] · Fix {Bug} #123 ended without completing (worker process exited with code 137 (SIGKILL)). Checkpoint saved.", doing);
    }

    // --- Part 2: Edge Cases for Project Resolution, Order Parsing, and Order Models ---

    [Fact]
    public void PickAskedProject_WhitespaceAndNullSafety()
    {
        var p1 = new ProjectSummary("proj-1", "Alpha Project");
        var snap = new OrchestratorSnapshot(new[] { p1 });

        // Whitespace around explicit projectId is trimmed
        var match = OrchestratorOrderParsing.PickAskedProject(snap, "  proj-1  ", "test");
        Assert.NotNull(match);
        Assert.Equal("proj-1", match.Id);

        // Snapshot with null / empty fields does not throw
        var emptyFieldProj = new ProjectSummary("", null!);
        var weirdSnap = new OrchestratorSnapshot(new[] { emptyFieldProj });
        Assert.Null(OrchestratorOrderParsing.PickAskedProject(weirdSnap, "nonexistent", "hello"));
        Assert.Equal("", OrchestratorOrderParsing.PickAskedProject(weirdSnap, null, "hello")?.Id);

        // Null Projects in snapshot
        var nullProjectsSnap = new OrchestratorSnapshot(null);
        Assert.Null(OrchestratorOrderParsing.PickAskedProject(nullProjectsSnap, "proj-1", "test"));
    }

    [Fact]
    public void ParseOrder_ConflictingToggleTokens_ReturnsNull()
    {
        var models = new[] { new ModelCatalogEntry("luna", "Luna") };
        var snap = new OrchestratorSnapshot(Models: models);

        // Both on and off tokens present: neither condition matches
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Turn on and off Luna", snap, null));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("accendi e spegni Luna", snap, null));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("disable and enable Luna", snap, null));
    }

    [Fact]
    public void ParseOrder_ModelLabelLengthConstraint()
    {
        // Model with label length <= 2: cannot match by label, but matches by Id
        var shortLabelModel = new ModelCatalogEntry("ai-model", "AI");
        var snap = new OrchestratorSnapshot(Models: new[] { shortLabelModel });

        // Mentioning short label "AI" does not trigger model toggle
        Assert.Null(OrchestratorOrderParsing.ParseOrder("Turn off AI", snap, null));

        // Mentioning full id "ai-model" does trigger model toggle
        var byId = OrchestratorOrderParsing.ParseOrder("Turn off ai-model", snap, null);
        Assert.NotNull(byId);
        var toggle = Assert.IsType<ModelToggleOrder>(byId);
        Assert.Equal("ai-model", toggle.Id);
        Assert.False(toggle.Enabled);
    }

    [Fact]
    public void ParseOrder_LifecycleWithOnSyntax_AndTargetMismatch()
    {
        var p1 = new ProjectSummary("demo", "Demo");
        var snap = new OrchestratorSnapshot(Projects: new[] { p1 });

        // "stop on Demo" and "start on demo" strip "on "
        var stopOn = OrchestratorOrderParsing.ParseOrder("stop on Demo!", snap, null);
        Assert.NotNull(stopOn);
        Assert.Equal("stop", Assert.IsType<LifecycleOrder>(stopOn).Action);
        Assert.Equal("demo", stopOn.ProjectId);

        var startOn = OrchestratorOrderParsing.ParseOrder("start on Demo.", snap, null);
        Assert.NotNull(startOn);
        Assert.Equal("start", Assert.IsType<LifecycleOrder>(startOn).Action);

        // Target mismatch: command names non-matching target
        Assert.Null(OrchestratorOrderParsing.ParseOrder("stop on OtherProject", snap, "demo"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("ferma NonExistent", snap, "demo"));
    }

    [Fact]
    public void ParseOrder_SavingAndChatModes_EdgeCases()
    {
        var snap = new OrchestratorSnapshot();

        // Multiple whitespace in saving mode
        var savingMulti = OrchestratorOrderParsing.ParseOrder("mode    safe", snap, "p1");
        Assert.NotNull(savingMulti);
        Assert.Equal("safe", Assert.IsType<SavingModeOrder>(savingMulti).SavingMode);

        var italianSavingMulti = OrchestratorOrderParsing.ParseOrder("modalità    ultra    sicura", snap, "p1");
        Assert.NotNull(italianSavingMulti);
        Assert.Equal("ultra-safe", Assert.IsType<SavingModeOrder>(italianSavingMulti).SavingMode);

        // "switch to auto start" must NOT match chat mode auto
        Assert.Null(OrchestratorOrderParsing.ParseOrder("switch to auto start", snap, "p1"));

        // Unknown saving or chat modes return null
        Assert.Null(OrchestratorOrderParsing.ParseOrder("mode turbo", snap, "p1"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("switch to quantum", snap, "p1"));
        Assert.Null(OrchestratorOrderParsing.ParseOrder("modalità super", snap, "p1"));
    }

    [Fact]
    public void ParsedOrder_Serialization_AndGoldModelPreservation()
    {
        // LifecycleOrder serialization
        var lifeWithName = new LifecycleOrder("start", "p1", "Project One") { GoldModel = "gold-v1" };
        var lifeJson = lifeWithName.ToJsonObject();
        Assert.Equal("start", lifeJson["action"]?.GetValue<string>());
        Assert.Equal("p1", lifeJson["projectId"]?.GetValue<string>());
        Assert.Equal("Project One", lifeJson["name"]?.GetValue<string>());
        Assert.Equal("gold-v1", lifeJson["goldModel"]?.GetValue<string>());

        var lifeWithoutName = new LifecycleOrder("stop", "p1", null);
        var noNameJson = lifeWithoutName.ToJsonObject();
        Assert.False(noNameJson.ContainsKey("name"));
        Assert.False(noNameJson.ContainsKey("goldModel"));

        // ChatModeOrder serialization
        var chatOrder = new ChatModeOrder("manual", "plan", "p2") { GoldModel = "custom-lead" };
        var chatJson = chatOrder.ToJsonObject();
        Assert.Equal("mode", chatJson["action"]?.GetValue<string>());
        Assert.Equal("manual", chatJson["mode"]?.GetValue<string>());
        Assert.Equal("plan", chatJson["chatMode"]?.GetValue<string>());
        Assert.Equal("p2", chatJson["projectId"]?.GetValue<string>());
        Assert.Equal("custom-lead", chatJson["goldModel"]?.GetValue<string>());

        // RejectOrder serialization
        var reject = new RejectOrder("pipeline", "p3");
        var rejectJson = reject.ToJsonObject();
        Assert.Equal("reject", rejectJson["action"]?.GetValue<string>());
        Assert.Equal("pipeline", rejectJson["mode"]?.GetValue<string>());
        Assert.Equal("p3", rejectJson["projectId"]?.GetValue<string>());

        // SystemQueryOrder serialization
        var sysOrder = new SystemQueryOrder("quota", "p1");
        var sysJson = sysOrder.ToJsonObject();
        Assert.Equal("quota", sysJson["action"]?.GetValue<string>());
        Assert.Equal("p1", sysJson["projectId"]?.GetValue<string>());
    }

    // --- Part 3: Edge Cases, Error Handling, and Recovery in RouteAsk and Snapshot Parsing ---

    [Fact]
    public void RouteAsk_Phase2_ExplicitlyNamedCommand_BindsRecipient()
    {
        var snap = new OrchestratorSnapshot(new[]
        {
            new ProjectSummary("p1", "Alpha"),
            new ProjectSummary("p2", "Beta")
        });

        // Bare command without project recipient fails on multi-project snapshot
        var bareStop = OrchestratorOrderParsing.RouteAsk("stop", snap, null);
        Assert.False(bareStop.Ok);
        Assert.Equal("Select a project or name it explicitly before issuing a command.", bareStop.Reason);

        // Explicitly named command "stop Alpha" binds project recipient
        var namedStop = OrchestratorOrderParsing.RouteAsk("stop Alpha", snap, null);
        Assert.True(namedStop.Ok);
        Assert.NotNull(namedStop.Order);
        Assert.Equal("stop", namedStop.Order.Action);
        Assert.Equal("p1", namedStop.Order.ProjectId);
        Assert.Equal("Alpha", namedStop.TargetProject?.Name);

        // Phase2 binds effectiveProjectId so conversation history for p1 is retrieved
        var conversation = new[]
        {
            new TurnHistory("Let's plan auth.", "Sure.", ProjectId: "p1", GrillKind: "docs")
        };

        var phase2Opts = new AskRoutingOptions(Conversation: conversation, Phase2: true);
        var phase2Res = OrchestratorOrderParsing.RouteAsk("stop Alpha", snap, null, phase2Opts);
        Assert.True(phase2Res.Ok);
        Assert.Equal("stop", phase2Res.Order?.Action);
        Assert.Equal("p1", phase2Res.Order?.ProjectId);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("quota")]
    public void RouteAsk_GoldModel_OnSystemQueries_Fails(string question)
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });
        var res = OrchestratorOrderParsing.RouteAsk(question, snap, "p1", null, goldModel: "gemini-pro");
        Assert.False(res.Ok);
        Assert.Equal("Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.", res.Reason);
    }

    [Theory]
    [InlineData("grill me!")]
    [InlineData("grill me.")]
    [InlineData("grill with docs?")]
    [InlineData("grill with the docs.")]
    [InlineData("GRILL WITH THE DOCS!")]
    public void RouteAsk_GrillCommands_PunctuationAndCaseInsensitive(string question)
    {
        var snap = new OrchestratorSnapshot(new[] { new ProjectSummary("p1", "Alpha") });
        var res = OrchestratorOrderParsing.RouteAsk(question, snap, "p1");
        Assert.True(res.Ok);
        Assert.NotNull(res.GrillKind);
        Assert.Null(res.Order); // Orders superseded by Grill
    }

    [Fact]
    public void RouteAsk_MalformedSnapshot_RecoveryAndNullSafety()
    {
        // Snapshot with null collections does not crash
        var emptySnap = new OrchestratorSnapshot(null, null, null, null, null);
        var res = OrchestratorOrderParsing.RouteAsk("status", emptySnap, null);
        Assert.True(res.Ok);
        Assert.Equal("state", res.Order?.Action);

        var modelRes = OrchestratorOrderParsing.RouteAsk("Turn off Luna", emptySnap, null);
        Assert.True(modelRes.Ok);
        Assert.Null(modelRes.Order); // No models available, not recognized as order

        // JSON parsing robustness on malformed nodes
        Assert.Null(ModelCatalogEntry.FromJson(null));
        Assert.Null(ModelCatalogEntry.FromJson(new JsonArray()));
        Assert.Null(ProjectSummary.FromJson(null));
        Assert.Null(ProjectSummary.FromJson(new JsonArray()));

        var malformedSnap = OrchestratorSnapshot.FromJson(new JsonObject
        {
            ["projects"] = "not an array",
            ["models"] = 12345
        });
        Assert.Null(malformedSnap.Projects);
        Assert.Null(malformedSnap.Models);

        var routeMalformed = OrchestratorOrderParsing.RouteAsk("status", malformedSnap, null);
        Assert.True(routeMalformed.Ok);
        Assert.Equal("state", routeMalformed.Order?.Action);
    }

    // --- Part 4: Concurrency and Thread-Safety Stress Tests ---

    [Fact]
    public void Concurrency_ParallelAnalyticalCalls_AreThreadSafeAndDeterministic()
    {
        var snap = new OrchestratorSnapshot(
            Projects: new[]
            {
                new ProjectSummary("p1", "Alpha", "idle", "talk"),
                new ProjectSummary("p2", "Beta", "running", "plan")
            },
            Models: new[]
            {
                new ModelCatalogEntry("luna", "Luna"),
                new ModelCatalogEntry("grok", "Grok")
            });

        var history = new[]
        {
            new TurnHistory("Should we research options?", "Yes, we can explore.", ProjectId: "p1", GrillKind: "docs")
        };

        // Run 500 concurrent operations in parallel
        Parallel.For(0, 500, i =>
        {
            switch (i % 8)
            {
                case 0:
                    Assert.True(OrchestratorOrderParsing.LooksLikeQuestion("what is the lead model?"));
                    Assert.False(OrchestratorOrderParsing.LooksLikeQuestion("proceed with work"));
                    break;
                case 1:
                    Assert.True(OrchestratorOrderParsing.LooksLikeWork("metti i worker sul job"));
                    Assert.False(OrchestratorOrderParsing.LooksLikeWork("How far did we go?"));
                    break;
                case 2:
                    Assert.True(OrchestratorOrderParsing.FreshTaskIntent("please implement the feature"));
                    Assert.False(OrchestratorOrderParsing.FreshTaskIntent("hello there"));
                    break;
                case 3:
                    var info = OrchestratorOrderParsing.CheckpointTalk("application update");
                    Assert.Equal("Saved unfinished work before updating.", info.Orch);
                    break;
                case 4:
                    Assert.True(OrchestratorOrderParsing.ResearchIntent("cerca documentazione"));
                    Assert.True(OrchestratorOrderParsing.ResearchIntent("yes", history));
                    break;
                case 5:
                    var proj = OrchestratorOrderParsing.PickAskedProject(snap, null, "Stop Beta now");
                    Assert.NotNull(proj);
                    Assert.Equal("p2", proj.Id);
                    break;
                case 6:
                    var order = OrchestratorOrderParsing.ParseOrder("Turn off Luna", snap, null);
                    Assert.NotNull(order);
                    var toggle = Assert.IsType<ModelToggleOrder>(order);
                    Assert.False(toggle.Enabled);
                    break;
                case 7:
                    var route = OrchestratorOrderParsing.RouteAsk("status", snap, "p1");
                    Assert.True(route.Ok);
                    Assert.Equal("state", route.Order?.Action);
                    break;
            }
        });
    }

    [Fact]
    public async Task Concurrency_AsyncMultiThreadedStressTest()
    {
        var snap = new OrchestratorSnapshot(
            Projects: new[] { new ProjectSummary("p1", "Alpha", "idle", "plan") },
            Models: new[] { new ModelCatalogEntry("luna", "Luna") });

        var tasks = Enumerable.Range(0, 100).Select(async id =>
        {
            await Task.Yield();
            var parsed = OrchestratorOrderParsing.ParseOrder(id % 2 == 0 ? "mode safe" : "mode talk", snap, "p1");
            Assert.NotNull(parsed);
            if (id % 2 == 0)
                Assert.Equal("safe", Assert.IsType<SavingModeOrder>(parsed).SavingMode);
            else
                Assert.Equal("talk", Assert.IsType<ChatModeOrder>(parsed).Mode);

            var routed = OrchestratorOrderParsing.RouteAsk("Turn on Luna", snap, "p1");
            Assert.True(routed.Ok);
            Assert.NotNull(routed.Order);
            Assert.True(Assert.IsType<ModelToggleOrder>(routed.Order).Enabled);
        });

        await Task.WhenAll(tasks);
    }
}
