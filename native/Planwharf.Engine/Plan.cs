// Port of plan.js.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Pure notebook edits. The caller passes every timestamp. This port does not read the clock.
public static class Plan
{
    public static readonly string[] GenericFrontier =
    [
        "What is the smallest next slice that still serves the current goal?",
        "What is explicitly out of scope for this slice?",
        "How will we know this slice is done?",
        "Which assumption, if wrong, would waste the most work?",
        "What must stay true (local, existing files, no paid APIs)?"
    ];

    static readonly string[] RevisionModes = ["manual", "grill", "import"];
    static readonly string[] DiffFields = ["direction", "state", "closed", "open"];
    static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Regex FolderDrive = new(@"^[A-Za-z]:[\\/]", RegexOptions.CultureInvariant);
    static readonly Regex LeadingDrive = new(@"^/[A-Za-z]:", RegexOptions.CultureInvariant);
    static readonly Regex GithubUrl = new(@"^https://[A-Za-z0-9.-]+(?::[0-9]{1,5})?(?:/[A-Za-z0-9_.-]+)*/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    static readonly Regex GithubSsh = new(@"^git@github\.com:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GithubWeb = new(@"^https?://(www\.)?github\.com/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex GithubParts = new(@"^https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?(?:/.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TrailingSlashes = new(@"/+$", RegexOptions.CultureInvariant);
    static readonly Regex NonLetterOrNumber = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    static readonly Regex EdgeHyphens = new(@"^-+|-+$", RegexOptions.CultureInvariant);
    static readonly Regex WrappedQuotes = new("^[\"']+|[\"']+$", RegexOptions.CultureInvariant);
    static readonly Regex IsoDate = new(@"^([+-]\d{6}|\d{4})(?:-(\d{2})(?:-(\d{2}))?)?(?:T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,9}))?)?(Z|[+-]\d{2}:\d{2})?)?$", RegexOptions.CultureInvariant);
    const long MaxSafeInteger = 9007199254740991;

    public static JsonObject Example() => new()
    {
        ["schema"] = 1,
        ["project"] = new JsonObject { ["id"] = "biblioteca", ["name"] = "Neighborhood library" },
        ["revisions"] = Arr(
            Revision(1, "manual", "Starting point", "2026-10-06T09:00:00+02:00",
                "Open a small neighborhood library accessible to everyone.",
                "Two possible locations identified. Initial collection: 120 donated books.",
                ["Free borrowing for residents.", "Start with a single location."],
                ["Which location should we choose?", "Who covers the first opening shift?"]),
            Revision(2, "manual", "One location, a first step", "2026-10-07T10:30:00+02:00",
                "Open the library on Harbor Street for a one-month trial.",
                "Harbor Street selected. 120 books cataloged. The trial schedule is being prepared.",
                ["Free borrowing for residents.", "Start with a single location.", "Use the Harbor Street location."],
                ["Who covers the first opening shift?", "Should we confirm Saturday morning opening?"])),
        ["tasks"] = Arr(new JsonObject
        {
            ["id"] = "calendario",
            ["projectId"] = "biblioteca",
            ["revision_id"] = 2,
            ["title"] = "Prepare the trial-month schedule",
            ["detail"] = "Define four shifts and collect volunteer availability.",
            ["status"] = "running"
        })
    };

    public static bool IsFolderPath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 500 || value.Contains('\0')) return false;
        if (FolderDrive.IsMatch(value) || value.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        return value.StartsWith('/') && value.Length > 1;
    }

    public static bool IsGithubUrl(string? value)
    {
        if (value is null || !GithubUrl.IsMatch(value)) return false;
        foreach (var part in value.Split('/'))
            if (part == "." || part == "..") return false;
        return true;
    }

    public static bool Validate(JsonNode? data)
    {
        if (data is JsonObject obj && IsNumber(obj["schema"], 1)) return ValidateProject(obj);
        if (data is not JsonObject notebook || !IsNumber(notebook["schema"], 2) || notebook["projects"] is not JsonArray projects || projects.Count == 0) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project is not JsonObject item || !ValidateProject(item)) return false;
            var id = StringProp(item["project"] as JsonObject, "id");
            if (id is null || !ids.Add(id)) return false;
        }
        return true;
    }

    public static bool ValidateProject(JsonNode? data)
    {
        if (data is not JsonObject obj) return false;
        if (obj.ContainsKey("home") && !ValidHome(obj["home"])) return false;
        if (!IsNumber(obj["schema"], 1) || obj["project"] is not JsonObject project) return false;
        if (!NonEmptyTrimmed(project, "id") || !NonEmptyTrimmed(project, "name")) return false;
        foreach (var pair in project)
            if (pair.Key is not ("id" or "name" or "folder" or "github")) return false;
        if (project.ContainsKey("folder") && !IsFolderPath(StringValue(project["folder"]))) return false;
        if (project.ContainsKey("github") && !IsGithubUrl(StringValue(project["github"]))) return false;
        if (obj["revisions"] is not JsonArray revisions || revisions.Count == 0 || obj["tasks"] is not JsonArray tasks) return false;

        var revisionIds = new HashSet<long>();
        foreach (var revisionNode in revisions)
        {
            if (revisionNode is not JsonObject revision || !IsSafeInteger(revision["id"], out var revisionId) || revisionId < 1 || !revisionIds.Add(revisionId)) return false;
            if (!TryString(revision["mode"], out var mode) || Array.IndexOf(RevisionModes, mode) < 0) return false;
            if (!HasString(revision, "title") || !TryString(revision["createdAt"], out var createdAt) || !IsFiniteDate(createdAt)) return false;
            if (!HasString(revision, "direction") || !HasString(revision, "state")) return false;
            if (revision["closed"] is not JsonArray closed || revision["open"] is not JsonArray open) return false;
            if (!AllStrings(closed) || !AllStrings(open)) return false;
        }

        var running = false;
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        var projectId = StringProp(project, "id")!;
        foreach (var taskNode in tasks)
        {
            if (taskNode is not JsonObject task || !TryString(task["id"], out var taskId) || taskId.Trim().Length == 0 || !taskIds.Add(taskId)) return false;
            if (StringProp(task, "projectId") != projectId || !IsSafeInteger(task["revision_id"], out var revisionId) || !revisionIds.Contains(revisionId)) return false;
            if (!HasString(task, "title") || !HasString(task, "detail") || !HasString(task, "status")) return false;
            if (task.ContainsKey("timing"))
            {
                if (!TryString(task["timing"], out var timing) || (timing != "deadline" && timing != "discretion")) return false;
                if (timing == "deadline" && (!TryString(task["deadlineAt"], out var deadline) || !IsFiniteDate(deadline))) return false;
            }
            if (StringProp(task, "status") == "running")
            {
                if (running) return false;
                running = true;
            }
        }
        return true;
    }

    static bool ValidHome(JsonNode? home)
    {
        if (home is not JsonObject h || h["notes"] is not JsonArray notes || notes.Count > 1000) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var noteNode in notes)
        {
            if (noteNode is not JsonObject note || !TryString(note["id"], out var id) || id.Length == 0 || !ids.Add(id)) return false;
            if (!TryString(note["text"], out var text) || text.Trim().Length == 0 || text.Length > 10000) return false;
            if (!TryString(note["createdAt"], out var createdAt) || !IsFiniteDate(createdAt)) return false;
            if (note.ContainsKey("pinned") && !IsBoolean(note["pinned"])) return false;
            if (note.ContainsKey("aiRelevant") && !IsBoolean(note["aiRelevant"])) return false;
        }
        // null and a missing lastVisit are the same under JavaScript != null.
        if (!h.TryGetPropertyValue("lastVisit", out var lastVisit) || lastVisit is null) return true;
        if (lastVisit is not JsonObject visit || !TryString(visit["viewedAt"], out var viewedAt) || !IsFiniteDate(viewedAt)) return false;
        if (visit["revisionIds"] is not JsonArray revisionIds) return false;
        foreach (var revisionId in revisionIds)
            if (!IsSafeInteger(revisionId, out _)) return false;
        return true;
    }

    static JsonObject Revision(long id, string mode, string title, string createdAt, string direction, string state, string[] closed, string[] open) => new()
    {
        ["id"] = id,
        ["mode"] = mode,
        ["title"] = title,
        ["createdAt"] = createdAt,
        ["direction"] = direction,
        ["state"] = state,
        ["closed"] = Strings(closed),
        ["open"] = Strings(open)
    };

    static JsonArray Strings(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) Push(array, Text(value));
        return array;
    }

    static string NormalizeFolder(string value)
    {
        var folder = WrappedQuotes.Replace(value.Trim(), "").Trim();
        if (folder.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) folder = folder[7..];
        if (LeadingDrive.IsMatch(folder)) folder = folder[1..];
        return folder;
    }

    static string NormalizeGithub(string value)
    {
        var github = GithubWeb.Replace(GithubSsh.Replace(value.Trim(), "https://github.com/"), "https://github.com/");
        var match = GithubParts.Match(github);
        if (!match.Success) return TrailingSlashes.Replace(github, "");
        var repo = match.Groups[2].Value;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return "https://github.com/" + match.Groups[1].Value + "/" + repo;
    }

    static JsonObject PlaceFrom(JsonObject input)
    {
        var folder = input["folder"] is JsonValue && TryString(input["folder"], out var folderText) ? NormalizeFolder(folderText) : "";
        var github = input["github"] is JsonValue && TryString(input["github"], out var githubText) ? NormalizeGithub(githubText) : "";
        if (folder.Length > 0 && !IsFolderPath(folder)) throw Error("Folder must be an absolute path on this PC.");
        if (github.Length > 0 && !IsGithubUrl(github)) throw Error("Repository must be an HTTPS GitHub or Gitea owner/repository URL.");
        return new JsonObject { ["folder"] = folder, ["github"] = github };
    }

    static JsonObject ProjectRecord(string id, string name, JsonObject place)
    {
        var project = new JsonObject { ["id"] = id, ["name"] = name };
        var folder = StringProp(place, "folder") ?? "";
        var github = StringProp(place, "github") ?? "";
        if (folder.Length > 0) project["folder"] = folder;
        if (github.Length > 0) project["github"] = github;
        return project;
    }

    static string ProjectIdFromName(string name, HashSet<string> used)
    {
        var lowered = name.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormKD);
        var slug = EdgeHyphens.Replace(NonLetterOrNumber.Replace(lowered, "-"), "");
        var baseId = slug.Length == 0 ? "project" : (slug.Length <= 48 ? slug : slug[..48]);
        if (!used.Contains(baseId)) return baseId;
        var n = 2;
        while (used.Contains(baseId + "-" + n.ToString(CultureInfo.InvariantCulture))) n++;
        return baseId + "-" + n.ToString(CultureInfo.InvariantCulture);
    }

    static bool IsNumber(JsonNode? node, double expected) => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && Number(value) == expected;

    static bool IsBoolean(JsonNode? node) => node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    static bool IsSafeInteger(JsonNode? node, out long integer)
    {
        integer = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        var number = Number(value);
        if (!double.IsFinite(number) || number != Math.Truncate(number) || Math.Abs(number) > MaxSafeInteger) return false;
        integer = (long)number;
        return true;
    }

    static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static bool TryString(JsonNode? node, out string text)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var found))
        {
            text = found;
            return true;
        }
        text = "";
        return false;
    }

    static string? StringValue(JsonNode? node) => TryString(node, out var text) ? text : null;

    static string? StringProp(JsonObject? obj, string key) => obj is not null && TryString(obj[key], out var text) ? text : null;

    static bool HasString(JsonObject obj, string key) => TryString(obj[key], out _);

    static bool NonEmptyTrimmed(JsonObject obj, string key) => TryString(obj[key], out var text) && text.Trim().Length > 0;

    static bool AllStrings(JsonArray array)
    {
        foreach (var item in array)
            if (!TryString(item, out _)) return false;
        return true;
    }

    static JsonObject CloneObject(JsonObject value) => (JsonObject)value.DeepClone();

    static JsonArray CloneArray(JsonArray value) => (JsonArray)value.DeepClone();

    static string Stringify(JsonNode? node) => node is null ? "null" : node.ToJsonString(Json);

    static bool SameJson(JsonNode? left, JsonNode? right) => Stringify(left) == Stringify(right);

    static bool IsFiniteDate(string text) => !double.IsNaN(DateParse(text));

    // Date.parse for the ISO format JavaScript defines. A date alone is UTC. A time without an offset is local.
    static double DateParse(string text)
    {
        var match = IsoDate.Match(text);
        if (!match.Success)
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed.ToUnixTimeMilliseconds() : double.NaN;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int Part(int group, int fallback) => match.Groups[group].Success ? int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;
        int month = Part(2, 1), day = Part(3, 1), hour = Part(4, 0), minute = Part(5, 0), second = Part(6, 0);
        var fraction = match.Groups[7].Success ? match.Groups[7].Value.PadRight(3, '0')[..3] : "000";
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 24 || minute > 59 || second > 59) return double.NaN;
        if (hour == 24 && (minute != 0 || second != 0 || fraction != "000")) return double.NaN;
        var local = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified).AddHours(hour).AddMinutes(minute).AddSeconds(second).AddMilliseconds(int.Parse(fraction, CultureInfo.InvariantCulture));
        TimeSpan offset;
        if (match.Groups[8].Success)
        {
            var zone = match.Groups[8].Value;
            offset = zone == "Z" ? TimeSpan.Zero : (zone[0] == '-' ? -1 : 1) * new TimeSpan(int.Parse(zone.Substring(1, 2), CultureInfo.InvariantCulture), int.Parse(zone.Substring(4, 2), CultureInfo.InvariantCulture), 0);
        }
        else offset = match.Groups[4].Success ? TimeZoneInfo.Local.GetUtcOffset(local) : TimeSpan.Zero;
        return (local - offset - DateTime.UnixEpoch).TotalMilliseconds;
    }

    static string ToIsoString(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || Math.Abs(milliseconds) > 8.64e15) throw Error("Invalid time value");
        var date = DateTime.UnixEpoch.AddMilliseconds(Math.Truncate(milliseconds));
        if (date.Year is < 0 or > 9999) throw Error("Invalid time value");
        return date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    public static JsonObject AppendRevision(JsonObject data, JsonObject? draft, string timestamp) => Append(data, draft, "manual", timestamp);

    static JsonObject Append(JsonObject data, JsonObject? draft, string mode, string timestamp)
    {
        if (!ValidateProject(data)) throw Error("The saved notebook is invalid.");
        if (draft is null || !NonEmptyTrimmed(draft, "title") || !NonEmptyTrimmed(draft, "direction") || !NonEmptyTrimmed(draft, "state") || draft["closed"] is not JsonArray closed || draft["open"] is not JsonArray open || !AllStrings(closed) || !AllStrings(open))
            throw Error("Fill in title, goal, and where it stands.");
        var next = CloneObject(data);
        long max = 0;
        foreach (var revision in data["revisions"]!.AsArray())
            if (IsSafeInteger(revision!["id"], out var id) && id > max) max = id;
        var revisionObject = new JsonObject
        {
            ["id"] = max + 1,
            ["mode"] = mode,
            ["title"] = StringProp(draft, "title")!.Trim(),
            ["createdAt"] = timestamp,
            ["direction"] = StringProp(draft, "direction")!.Trim(),
            ["state"] = StringProp(draft, "state")!.Trim(),
            ["closed"] = TrimmedStrings(closed),
            ["open"] = TrimmedStrings(open)
        };
        Push(next["revisions"]!.AsArray(), revisionObject);
        if (!Validate(next)) throw Error("The new revision is invalid.");
        return next;
    }

    static JsonArray TrimmedStrings(JsonArray values)
    {
        var array = new JsonArray();
        foreach (var item in values)
        {
            var text = StringValue(item)!.Trim();
            if (text.Length > 0) Push(array, Text(text));
        }
        return array;
    }

    public static bool IsGenericFrontier(JsonNode? open)
    {
        if (open is not JsonArray questions || questions.Count != GenericFrontier.Length) return false;
        for (var i = 0; i < GenericFrontier.Length; i++)
            if (StringValue(questions[i]) != GenericFrontier[i]) return false;
        return true;
    }

    public static JsonArray GrillFrontier(JsonObject? context)
    {
        var ctx = context ?? new JsonObject();
        if (ctx["questions"] is JsonArray supplied && supplied.Count > 0)
        {
            var picked = new JsonArray();
            foreach (var item in supplied)
            {
                var text = JsString(item).Trim();
                if (text.Length == 0) continue;
                Push(picked, Text(text));
                if (picked.Count == 8) break;
            }
            return picked;
        }
        var name = JsString(Truthy(ctx["name"]) ? ctx["name"] : JsonValue.Create("this project")).Trim();
        if (name.Length == 0) name = "this project";
        var goal = JsString(Truthy(ctx["goal"]) ? ctx["goal"] : JsonValue.Create("")).Trim();
        var folder = JsString(Truthy(ctx["folder"]) ? ctx["folder"] : JsonValue.Create("")).Trim();
        var docs = new List<string>();
        if (ctx["docs"] is JsonArray docNodes)
        {
            foreach (var item in docNodes)
            {
                var text = JsString(item).Trim();
                if (text.Length == 0) continue;
                docs.Add(text);
                if (docs.Count == 12) break;
            }
        }
        var questions = Arr(
            Text(goal.Length > 0
                ? $"Is this run the next slice of {name}: {goal}? Recommended: yes."
                : $"Is this run the next slice of {name}? Recommended: yes, stay on the notebook goal."),
            Text(folder.Length > 0
                ? $"Stay inside {folder} and the files already there. No paid APIs. Recommended: yes."
                : "Keep this local: existing files, no paid APIs. Recommended: yes."),
            Text("Is the slice done when the work is on disk with tests, and nothing extra ships? Recommended: yes."),
            Text("Is everything else out of scope until this slice is done? Recommended: yes."));
        if (docs.Count > 0) Push(questions, Text($"Should {string.Join(", ", docs)} bound this slice? Recommended: yes, those docs are the authority."));
        return questions;
    }

    public static JsonArray StarterFrontier() => GrillFrontier(null);

    public static JsonArray DocsFrontier(JsonNode? docs, JsonObject? context)
    {
        var names = new JsonArray();
        if (docs is JsonArray list)
        {
            foreach (var item in list)
            {
                var text = JsString(item).Trim();
                if (text.Length == 0) continue;
                Push(names, Text(text));
                if (names.Count == 12) break;
            }
        }
        var merged = context is null ? new JsonObject() : CloneObject(context);
        merged["docs"] = names;
        return GrillFrontier(merged);
    }

    static JsonObject BeginGrill(JsonObject data, string title, JsonArray open, string timestamp, bool replace)
    {
        if (!ValidateProject(data)) throw Error("The saved notebook is invalid.");
        var revisions = data["revisions"]!.AsArray();
        var latest = revisions.Count == 0 ? null : revisions[^1] as JsonObject;
        if (latest is null) throw Error("Cannot read properties of undefined (reading 'open')");
        var latestOpen = latest["open"] as JsonArray;
        if (latestOpen is { Count: > 0 } && !replace && !IsGenericFrontier(latestOpen)) return data;
        var direction = StringProp(latest, "direction") ?? "";
        if (direction.Trim().Length == 0) throw Error("Fill in title, goal, and where it stands.");
        var previous = CloneArray(revisions);
        var next = CloneObject(data);
        long max = 0;
        foreach (var revision in revisions)
            if (revision is JsonObject row && IsSafeInteger(row["id"], out var id) && id > max) max = id;
        Push(next["revisions"]!.AsArray(), new JsonObject
        {
            ["id"] = max + 1,
            ["mode"] = "grill",
            ["title"] = title,
            ["createdAt"] = timestamp,
            ["direction"] = direction,
            ["state"] = StringProp(latest, "state") ?? "",
            ["closed"] = latest["closed"] is JsonArray closed ? CloneArray(closed) : new JsonArray(),
            ["open"] = CloneArray(open)
        });
        var kept = next["revisions"]!.AsArray();
        var withoutNew = new JsonArray();
        for (var i = 0; i < kept.Count - 1; i++) withoutNew.Add(kept[i]!.DeepClone());
        if (!Validate(next) || !SameJson(withoutNew, previous) || !SameJson(next["tasks"], data["tasks"]))
            throw Error("The updated notebook is invalid.");
        return next;
    }

    public static JsonObject StartGrill(JsonObject data, string timestamp, JsonObject? context = null)
    {
        var revisions = RequireRevisions(data);
        var latest = Latest(revisions);
        var questions = GrillFrontier(SpreadContext(data, latest, context));
        var replace = Truthy(context?["replace"]) || IsGenericFrontier(latest["open"]);
        return BeginGrill(data, "Grill me", questions, timestamp, replace);
    }

    public static JsonObject StartGrillDocs(JsonObject data, JsonNode? docs, string timestamp, JsonObject? context = null)
    {
        var names = new JsonArray();
        if (docs is JsonArray list)
        {
            foreach (var item in list)
            {
                var text = JsString(item).Trim();
                if (text.Length == 0) continue;
                Push(names, Text(text));
                if (names.Count == 12) break;
            }
        }
        if (names.Count == 0) throw Error("No docs in the folder to grill with.");
        var revisions = RequireRevisions(data);
        var latest = Latest(revisions);
        var questions = DocsFrontier(names, SpreadContext(data, latest, context));
        var replace = Truthy(context?["replace"]) || IsGenericFrontier(latest["open"]);
        return BeginGrill(data, "Grill with the docs", questions, timestamp, replace);
    }

    public static JsonObject Grill(JsonObject data, JsonObject? round, string timestamp)
    {
        if (!ValidateProject(data)) throw Error("The saved notebook is invalid.");
        var latest = Latest(RequireRevisions(data));
        var open = latest["open"] as JsonArray;
        var answers = round?["answers"] as JsonArray;
        if (round is null || !NonEmptyTrimmed(round, "title") || answers is null || open is null || answers.Count != open.Count || open.Count == 0)
            throw Error("Answer the open questions of the current revision.");
        var draft = CloneObject(latest);
        draft["title"] = StringProp(round, "title");
        draft["open"] = new JsonArray();
        var directionChanges = 0;
        var responses = new JsonArray();
        for (var i = 0; i < open.Count; i++)
        {
            if (answers[i] is not JsonObject response || !TryString(open[i], out var question) || StringProp(response, "question") != question || !TryString(response["action"], out var action) || action is not ("keep" or "close" or "direction") || !TryString(response["answer"], out var answerText) || (action != "keep" && answerText.Trim().Length == 0))
                throw Error("Each answer must indicate the current question and a valid choice.");
            var answer = answerText.Trim();
            Push(responses, new JsonObject { ["question"] = question, ["action"] = action, ["answer"] = answer });
            if (action == "keep") Push(draft["open"]!.AsArray(), Text(question));
            else
            {
                Push(draft["closed"]!.AsArray(), Text(question + " → " + answer));
                if (action == "direction")
                {
                    if (++directionChanges > 1) throw Error("Choose only one new direction per round.");
                    draft["direction"] = answer;
                }
            }
        }
        var next = Append(data, draft, "grill", timestamp);
        next["revisions"]!.AsArray()[^1]!.AsObject()["responses"] = responses;
        return next;
    }

    public static JsonObject ImportRevision(JsonObject data, string json, string timestamp)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(json); }
        catch (JsonException) { throw Error("Invalid import JSON."); }
        const string importError = "Import an object with title, goal, where it stands, decided decisions, and open questions.";
        if (parsed is not JsonObject draft) throw Error(importError);
        foreach (var pair in draft)
            if (pair.Key is not ("id" or "mode" or "createdAt" or "title" or "direction" or "state" or "closed" or "open"))
                throw Error(importError);
        if (draft.ContainsKey("id") && (!IsSafeInteger(draft["id"], out var id) || id < 1)) throw Error(importError);
        if (draft.ContainsKey("mode") && (!TryString(draft["mode"], out var mode) || Array.IndexOf(RevisionModes, mode) < 0)) throw Error(importError);
        if (draft.ContainsKey("createdAt") && (!TryString(draft["createdAt"], out var createdAt) || !IsFiniteDate(createdAt))) throw Error(importError);
        return Append(data, draft, "import", timestamp);
    }

    static JsonArray RequireRevisions(JsonObject data)
    {
        if (!data.TryGetPropertyValue("revisions", out var node) || node is null)
            throw Error("Cannot read properties of undefined (reading 'at')");
        if (node is not JsonArray revisions) throw Error("data.revisions.at is not a function");
        return revisions;
    }

    static JsonObject Latest(JsonArray revisions)
    {
        if (revisions.Count == 0 || revisions[^1] is not JsonObject latest)
            throw Error("Cannot read properties of undefined (reading 'open')");
        return latest;
    }

    static JsonObject SpreadContext(JsonObject data, JsonObject latest, JsonObject? context)
    {
        if (data["project"] is not JsonObject project) throw Error("Cannot read properties of undefined (reading 'name')");
        var merged = new JsonObject();
        if (project.ContainsKey("name")) merged["name"] = project["name"]?.DeepClone();
        if (latest.ContainsKey("direction")) merged["goal"] = latest["direction"]?.DeepClone();
        if (project.ContainsKey("folder")) merged["folder"] = project["folder"]?.DeepClone();
        if (context is not null)
            foreach (var pair in context) merged[pair.Key] = pair.Value?.DeepClone();
        return merged;
    }

    static bool Truthy(JsonNode? node)
    {
        if (node is not JsonValue value) return node is not null;
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) is var number && number != 0 && !double.IsNaN(number),
            JsonValueKind.True => true,
            _ => false
        };
    }

    static string JsString(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonArray array)
        {
            var parts = new string[array.Count];
            for (var i = 0; i < array.Count; i++) parts[i] = array[i] is null ? "" : JsString(array[i]);
            return string.Join(",", parts);
        }
        if (node is not JsonValue value) return "[object Object]";
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Number => JsNumber(value),
            _ => value.ToJsonString()
        };
    }

    static string JsNumber(JsonValue value)
    {
        var number = Number(value);
        if (double.IsNaN(number)) return "NaN";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == Math.Truncate(number) && Math.Abs(number) <= MaxSafeInteger) return ((long)number).ToString(CultureInfo.InvariantCulture);
        return number.ToString("R", CultureInfo.InvariantCulture);
    }

    public static JsonObject CreateProject(JsonNode? data, JsonObject? input, string timestamp)
    {
        if (data is not JsonObject existing || !Validate(existing)) throw Error("The saved notebook is invalid.");
        if (input is null || !NonEmptyTrimmed(input, "name") || !NonEmptyTrimmed(input, "direction")) throw Error("Fill in name and goal.");
        var place = PlaceFrom(input);
        var notebook = IsNumber(existing["schema"], 2)
            ? CloneObject(existing)
            : new JsonObject { ["schema"] = 2, ["projects"] = Arr(CloneObject(existing)) };
        return PushProject(notebook, NewProject(notebook, StringProp(input, "name")!.Trim(), ProjectIdFromName(StringProp(input, "name")!, UsedIds(notebook)), place, Arr(
            Revision(1, "manual", "", timestamp, StringProp(input, "direction")!.Trim(), "", [], [])
        ), new JsonArray(), null));
    }

    public static JsonObject RestoreProject(JsonNode? data, JsonObject? source, string timestamp) => RestoreProject(data, source, null, timestamp);

    public static JsonObject RestoreProject(JsonNode? data, JsonObject? source, JsonObject? place, string timestamp)
    {
        if (data is not JsonObject existing || !Validate(existing)) throw Error("The saved notebook is invalid.");
        if (source is null || !NonEmptyTrimmed(source, "name")) throw Error("Export is missing a project name.");
        var placeInput = new JsonObject
        {
            ["folder"] = place is not null && place.TryGetPropertyValue("folder", out var folder) ? folder?.DeepClone() : "",
            ["github"] = place is not null && place.TryGetPropertyValue("github", out var github) ? github?.DeepClone() : ""
        };
        var located = PlaceFrom(placeInput);
        var notebook = IsNumber(existing["schema"], 2)
            ? CloneObject(existing)
            : new JsonObject { ["schema"] = 2, ["projects"] = Arr(CloneObject(existing)) };
        var name = StringProp(source, "name")!.Trim();
        var id = ProjectIdFromName(name, UsedIds(notebook));
        JsonArray revisions;
        if (source["revisions"] is JsonArray sourceRevisions && sourceRevisions.Count > 0)
        {
            revisions = new JsonArray();
            foreach (var node in sourceRevisions)
            {
                if (node is null) throw Error("Cannot read properties of null (reading 'id')");
                var revision = node as JsonObject;
                var created = revision is not null && TryString(revision["createdAt"], out var createdText) && IsFiniteDate(createdText) ? createdText : timestamp;
                Push(revisions, new JsonObject
                {
                    ["id"] = revision?["id"]?.DeepClone(),
                    ["mode"] = revision is not null && TryString(revision["mode"], out var mode) && Array.IndexOf(RevisionModes, mode) >= 0 ? mode : "import",
                    ["title"] = revision is not null && TryString(revision["title"], out var title) ? title : "",
                    ["createdAt"] = created,
                    ["direction"] = revision is not null && TryString(revision["direction"], out var direction) ? direction : "",
                    ["state"] = revision is not null && TryString(revision["state"], out var state) ? state : "",
                    ["closed"] = StringItems(revision?["closed"]),
                    ["open"] = StringItems(revision?["open"])
                });
            }
        }
        else revisions = Arr(Revision(1, "import", "Imported project", timestamp, name, "", [], []));
        var tasks = new JsonArray();
        if (source["tasks"] is JsonArray sourceTasks)
        {
            foreach (var node in sourceTasks)
            {
                if (node is null) throw Error("Cannot read properties of null (reading 'id')");
                if (node is not JsonObject task)
                {
                    Push(tasks, new JsonObject());
                    continue;
                }
                var copy = new JsonObject();
                CopyIfPresent(task, copy, "id");
                copy["projectId"] = id;
                CopyIfPresent(task, copy, "revision_id");
                CopyIfPresent(task, copy, "title");
                CopyIfPresent(task, copy, "detail");
                if (StringProp(task, "status") == "running") copy["status"] = "paused";
                else CopyIfPresent(task, copy, "status");
                if (Truthy(task["timing"])) copy["timing"] = task["timing"]!.DeepClone();
                if (Truthy(task["deadlineAt"])) copy["deadlineAt"] = task["deadlineAt"]!.DeepClone();
                Push(tasks, copy);
            }
        }
        JsonObject? home = null;
        if (source["notes"] is JsonArray sourceNotes)
        {
            var notes = new JsonArray();
            foreach (var node in sourceNotes)
            {
                if (node is not JsonObject note || !TryString(note["id"], out var noteId) || noteId.Length == 0) continue;
                if (!TryString(note["text"], out var noteText) || noteText.Trim().Length == 0 || !TryString(note["createdAt"], out var noteCreated)) continue;
                var copy = new JsonObject { ["id"] = noteId, ["text"] = noteText, ["createdAt"] = noteCreated };
                if (note["pinned"] is JsonValue pinned && pinned.TryGetValue<bool>(out var pinnedValue) && pinnedValue) copy["pinned"] = true;
                if (note["aiRelevant"] is JsonValue relevant && relevant.TryGetValue<bool>(out var relevantValue) && relevantValue) copy["aiRelevant"] = true;
                Push(notes, copy);
            }
            if (notes.Count > 0) home = new JsonObject { ["notes"] = notes };
        }
        var item = NewProject(notebook, name, id, located, revisions, tasks, home);
        if (!ValidateProject(item)) throw Error("Import is not a valid Planwharf project.");
        return PushProject(notebook, item);
    }

    public static JsonObject SetPlace(JsonNode? data, JsonObject? input)
    {
        if (data is not JsonObject existing || !Validate(existing)) throw Error("The saved notebook is invalid.");
        if (input is null || !NonEmptyTrimmed(input, "id")) throw Error("Project not found.");
        var place = PlaceFrom(input);
        if (IsNumber(existing["schema"], 1))
        {
            var project = existing["project"] as JsonObject;
            if (StringProp(input, "id") != StringProp(project, "id")) throw Error("Project not found.");
            var next = CloneObject(existing);
            next["project"] = ProjectRecord(StringProp(project, "id")!, StringProp(project, "name")!, place);
            if (!Validate(next) || !SameJson(next["revisions"], existing["revisions"]) || !SameJson(next["tasks"], existing["tasks"]))
                throw Error("The updated notebook is invalid.");
            return next;
        }
        var notebook = CloneObject(existing);
        var projects = notebook["projects"]!.AsArray();
        var index = FindProject(projects, StringProp(input, "id")!);
        if (index < 0) throw Error("Project not found.");
        var previous = Snapshot(projects);
        var current = projects[index]!.AsObject();
        var currentProject = current["project"]!.AsObject();
        // Schema 2 rebuilds the project object and drops home. plan.js does this; do not keep home.
        projects[index] = new JsonObject
        {
            ["schema"] = current["schema"]?.DeepClone(),
            ["project"] = ProjectRecord(StringProp(currentProject, "id")!, StringProp(currentProject, "name")!, place),
            ["revisions"] = CloneArray(current["revisions"]!.AsArray()),
            ["tasks"] = CloneArray(current["tasks"]!.AsArray())
        };
        if (!Validate(notebook)) throw Error("The updated notebook is invalid.");
        for (var i = 0; i < previous.Count; i++)
        {
            var updated = projects[i]!.AsObject();
            if (i == index)
            {
                if (!SameJson(previous[i]["revisions"], updated["revisions"]) || !SameJson(previous[i]["tasks"], updated["tasks"]) || StringProp(previous[i]["project"] as JsonObject, "id") != StringProp(updated["project"] as JsonObject, "id") || StringProp(previous[i]["project"] as JsonObject, "name") != StringProp(updated["project"] as JsonObject, "name"))
                    throw Error("The updated notebook is invalid.");
            }
            else if (!SameJson(previous[i], updated)) throw Error("The updated notebook is invalid.");
        }
        return notebook;
    }

    public static JsonObject ResetProject(JsonNode? data, JsonObject? input, string timestamp)
    {
        if (data is not JsonObject existing || !Validate(existing)) throw Error("The saved notebook is invalid.");
        if (input is null || !NonEmptyTrimmed(input, "id") || !NonEmptyTrimmed(input, "direction")) throw Error("Project not found.");
        var notebook = CloneObject(existing);
        var schema2 = IsNumber(notebook["schema"], 2);
        // Schema 1 stores the notebook in a fresh array. Replacing that slot does not change the notebook.
        var items = schema2 ? notebook["projects"]!.AsArray() : Arr(notebook);
        var index = FindProject(items, StringProp(input, "id")!);
        if (index < 0) throw Error("Project not found.");
        var others = new List<JsonObject>();
        for (var i = 0; i < items.Count; i++)
            if (i != index) others.Add(CloneObject(items[i]!.AsObject()));
        items[index] = ReplaceHistory(items[index]!.AsObject(), timestamp, StringProp(input, "direction")!.Trim());
        if (!schema2)
        {
            if (!Validate(notebook)) throw Error("The updated notebook is invalid.");
            return notebook;
        }
        if (!Validate(notebook)) throw Error("The updated notebook is invalid.");
        var kept = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (i == index) continue;
            if (!SameJson(items[i], others[kept])) throw Error("The updated notebook is invalid.");
            kept++;
        }
        return notebook;
    }

    public static JsonObject RemoveProject(JsonNode? data, JsonObject? input)
    {
        if (data is not JsonObject existing || !Validate(existing)) throw Error("The saved notebook is invalid.");
        if (input is null || !NonEmptyTrimmed(input, "id")) throw Error("Project not found.");
        if (IsNumber(existing["schema"], 1) || existing["projects"] is not JsonArray || existing["projects"]!.AsArray().Count < 2)
            throw Error("The notebook must keep at least one project.");
        var notebook = CloneObject(existing);
        var projects = notebook["projects"]!.AsArray();
        var index = FindProject(projects, StringProp(input, "id")!);
        if (index < 0) throw Error("Project not found.");
        var remaining = new List<JsonObject>();
        for (var i = 0; i < projects.Count; i++)
            if (i != index) remaining.Add(CloneObject(projects[i]!.AsObject()));
        projects.RemoveAt(index);
        if (!Validate(notebook)) throw Error("The updated notebook is invalid.");
        for (var i = 0; i < remaining.Count; i++)
            if (!SameJson(remaining[i], projects[i])) throw Error("The updated notebook is invalid.");
        return notebook;
    }

    static HashSet<string> UsedIds(JsonObject notebook)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in notebook["projects"]!.AsArray())
        {
            var id = StringProp(item?["project"] as JsonObject, "id");
            if (id is not null) used.Add(id);
        }
        return used;
    }

    static JsonObject NewProject(JsonObject notebook, string name, string id, JsonObject place, JsonArray revisions, JsonArray tasks, JsonObject? home)
    {
        _ = notebook;
        var item = new JsonObject
        {
            ["schema"] = 1,
            ["project"] = ProjectRecord(id, name, place),
            ["revisions"] = revisions,
            ["tasks"] = tasks
        };
        if (home is not null) item["home"] = home;
        return item;
    }

    static JsonObject PushProject(JsonObject notebook, JsonObject item)
    {
        var projects = notebook["projects"]!.AsArray();
        var previous = Snapshot(projects);
        Push(projects, item);
        if (!Validate(notebook)) throw Error("The updated notebook is invalid.");
        for (var i = 0; i < previous.Count; i++)
            if (!SameJson(previous[i], projects[i])) throw Error("The updated notebook is invalid.");
        return notebook;
    }

    static List<JsonObject> Snapshot(JsonArray projects)
    {
        var previous = new List<JsonObject>(projects.Count);
        foreach (var item in projects) previous.Add(CloneObject(item!.AsObject()));
        return previous;
    }

    static int FindProject(JsonArray projects, string id)
    {
        for (var i = 0; i < projects.Count; i++)
            if (projects[i] is JsonObject item && StringProp(item["project"] as JsonObject, "id") == id) return i;
        return -1;
    }

    static JsonObject ReplaceHistory(JsonObject current, string timestamp, string direction)
    {
        var revisions = Arr(Revision(1, "manual", "", timestamp, direction, "", [], []));
        var tasks = new JsonArray();
        var next = new JsonObject();
        var sawRevisions = false;
        var sawTasks = false;
        foreach (var pair in current)
        {
            if (pair.Key == "revisions") { next["revisions"] = revisions; sawRevisions = true; }
            else if (pair.Key == "tasks") { next["tasks"] = tasks; sawTasks = true; }
            else next[pair.Key] = pair.Value?.DeepClone();
        }
        if (!sawRevisions) next["revisions"] = revisions;
        if (!sawTasks) next["tasks"] = tasks;
        return next;
    }

    static JsonArray StringItems(JsonNode? node)
    {
        var array = new JsonArray();
        if (node is JsonArray items)
            foreach (var item in items)
                if (TryString(item, out var text)) Push(array, Text(text));
        return array;
    }

    static void CopyIfPresent(JsonObject source, JsonObject target, string key)
    {
        if (source.ContainsKey(key)) target[key] = source[key]?.DeepClone();
    }

    public static JsonObject TaskTiming(JsonObject? input)
    {
        if (input is null || !input.TryGetPropertyValue("timing", out var timingNode) || timingNode is null || (TryString(timingNode, out var blank) && blank.Length == 0))
            return new JsonObject();
        if (!TryString(timingNode, out var timing) || (timing != "deadline" && timing != "discretion"))
            throw Error("Choose a deadline or leave timing to the worker.");
        if (timing == "discretion") return new JsonObject { ["timing"] = "discretion" };
        if (!TryString(input["deadlineAt"], out var deadline) || !IsFiniteDate(deadline))
            throw Error("Set the task deadline.");
        return new JsonObject { ["timing"] = "deadline", ["deadlineAt"] = ToIsoString(DateParse(deadline)) };
    }

    public static JsonObject CreateTask(JsonObject data, JsonObject? input)
    {
        if (!ValidateProject(data)) throw Error("The saved notebook is invalid.");
        foreach (var taskNode in data["tasks"]!.AsArray())
            if (StringProp(taskNode as JsonObject, "status") == "running")
                throw Error("This project already has a running task. No second task was created.");
        if (input is null || !IsSafeInteger(input["revision_id"], out var revisionId) || !RevisionExists(data, revisionId) || !NonEmptyTrimmed(input, "title") || !HasString(input, "detail"))
            throw Error("The task requires a title and an existing revision.");
        var extra = TaskTiming(input);
        var next = CloneObject(data);
        var tasks = next["tasks"]!.AsArray();
        var counter = 1;
        while (HasTaskId(tasks, "task-" + counter.ToString(CultureInfo.InvariantCulture))) counter++;
        var created = new JsonObject
        {
            ["id"] = "task-" + counter.ToString(CultureInfo.InvariantCulture),
            ["projectId"] = StringProp(data["project"] as JsonObject, "id"),
            ["revision_id"] = revisionId,
            ["title"] = StringProp(input, "title")!.Trim(),
            ["detail"] = StringProp(input, "detail")!.Trim(),
            ["status"] = "running"
        };
        foreach (var pair in extra) created[pair.Key] = pair.Value?.DeepClone();
        Push(tasks, created);
        return next;
    }

    public static JsonObject RemoveTask(JsonObject data, JsonObject? input)
    {
        if (!ValidateProject(data)) throw Error("The saved notebook is invalid.");
        if (input is null || !NonEmptyTrimmed(input, "id")) throw Error("Job not found.");
        var next = CloneObject(data);
        var tasks = next["tasks"]!.AsArray();
        var id = StringProp(input, "id");
        var index = -1;
        for (var i = 0; i < tasks.Count; i++)
            if (StringProp(tasks[i] as JsonObject, "id") == id) { index = i; break; }
        if (index < 0) throw Error("Job not found.");
        tasks.RemoveAt(index);
        if (!Validate(next) || !SameJson(next["revisions"], data["revisions"]) || !SameJson(next["project"], data["project"]))
            throw Error("The updated notebook is invalid.");
        return next;
    }

    public static JsonArray Diff(JsonObject? previous, JsonObject current)
    {
        var changes = new JsonArray();
        foreach (var key in DiffFields)
        {
            JsonNode? before = null;
            var hasBefore = previous is not null && previous.TryGetPropertyValue(key, out before);
            JsonNode? after = null;
            var hasAfter = current.TryGetPropertyValue(key, out after);
            if (!hasBefore && !hasAfter) continue;
            if (hasBefore == hasAfter && SameJson(before, after)) continue;
            var change = new JsonObject { ["key"] = key };
            if (hasBefore) change["before"] = before?.DeepClone();
            if (hasAfter) change["after"] = after?.DeepClone();
            Push(changes, change);
        }
        JsonArray? closed = null;
        if (previous is not null && previous.TryGetPropertyValue("closed", out var closedNode) && closedNode is not null)
        {
            if (closedNode is not JsonArray closedArray) throw Error("previous.closed.filter is not a function");
            closed = closedArray;
        }
        if (closed is { Count: > 0 })
        {
            if (!current.TryGetPropertyValue("open", out var openNode) || openNode is null)
                throw Error("Cannot read properties of undefined (reading 'some')");
            if (openNode is not JsonArray open) throw Error("current.open.some is not a function");
            var reopened = new JsonArray();
            foreach (var decisionNode in closed)
            {
                if (!TryString(decisionNode, out var decision)) throw Error("decision.trim is not a function");
                foreach (var questionNode in open)
                {
                    if (!TryString(questionNode, out var question)) throw Error("question.trim is not a function");
                    if (string.Equals(question.Trim().ToLower(Italian), decision.Trim().ToLower(Italian), StringComparison.Ordinal))
                    {
                        Push(reopened, Text(decision));
                        break;
                    }
                }
            }
            if (reopened.Count > 0)
                Push(changes, new JsonObject { ["key"] = "reopened", ["before"] = new JsonArray(), ["after"] = reopened });
        }
        return changes;
    }

    static bool RevisionExists(JsonObject data, long id)
    {
        foreach (var revision in data["revisions"]!.AsArray())
            if (revision is JsonObject row && IsSafeInteger(row["id"], out var found) && found == id) return true;
        return false;
    }

    static bool HasTaskId(JsonArray tasks, string id)
    {
        foreach (var task in tasks)
            if (StringProp(task as JsonObject, "id") == id) return true;
        return false;
    }

    // Add(JsonNode) stays AOT-safe. Add<T> of a JsonObject or a string is rejected by the trimmer.
    static void Push(JsonArray array, JsonNode? node) => array.Add(node);

    static JsonNode Text(string value) => JsonValue.Create(value)!;

    static JsonArray Arr(params JsonNode?[] items)
    {
        var array = new JsonArray();
        foreach (var item in items) array.Add(item);
        return array;
    }

    static InvalidOperationException Error(string message) => new(message);
}
