Give me more detail on the Resource document schemas problem


• Here’s the deeper dive on the Resource document schema compilation problem.

  What’s happening

  - Every POST/PUT request compiles the per-resource JSON Schema on-demand, per request.
  - Code path:
      - DocumentValidator.GetSchema(...):
          - Fetches JsonNode schema from ResourceSchema.JsonSchemaForRequestMethod(method).
          - Serializes that node to a string with JsonSerializer.Serialize(...).
          - Calls JsonSchema.FromText(stringifiedJsonSchema) to create a new compiled schema object.
      - Then Validate(...) creates a new EvaluationOptions and calls Evaluate(...).
      - Because the validator prunes data, it re-runs Evaluate up to three more times after each prune.

  Where this lives

  - src/dms/core/EdFi.DataManagementService.Core/Validation/DocumentValidator.cs:36–52
      - Compiles schema on each Validate via JsonSchema.FromText(...).
      - Creates new EvaluationOptions per call.
      - Re-evaluates after pruning: overposted data, nulls, and whitespace adjustments.

  What’s different from ApiSchema validation

  - ApiSchemaValidator (schema-of-schemas) compiles once and caches via Lazy<JsonSchema> and reuses static
    EvaluationOptions.
      - src/dms/core/EdFi.DataManagementService.Core/ApiSchema/ApiSchemaValidator.cs: private readonly Lazy<JsonSchema>
        _jsonSchema + static readonly EvaluationOptions

  Why this is costly

  - Per-request compilation:
      - Serializing the schema JsonNode to text allocates a large string (often tens of KB+ depending on resource size).
      - JsonSchema.FromText(...) parses and builds a new schema object every request.
      - Under concurrency, this burns CPU and LOH, even though schemas don’t change between reloads.
  - Multiple evaluations:
      - For POST/PUT, Evaluate(...) may run 2–4 times due to prune-and-revalidate flow. Even with the same compiled schema
        per request, this multiplies CPU.
  - Repeated allocations:
      - New EvaluationOptions objects on every call (and every re-evaluation).
      - Pruning path currently stringifies and reparses JSON bodies, amplifying the cost when paired with multiple
        evaluations.

  How often it happens

  - Upsert/Update pipelines:
      - POST (UpsertHandler path) and PUT (UpdateByIdHandler path) hit DocumentValidator.Validate every request.
      - GET/DELETE do not use DocumentValidator, so the impact is concentrated on write workloads (bulk load, ingestion).

  Estimated impact

  - Removing per-request schema compilation typically cuts write-request CPU by several milliseconds per request (resource-
    dependent) and eliminates large transient string allocations.
  - Reusing a single EvaluationOptions instance removes micro-allocations and reduces GC churn.
  - If we also stop stringifying/reparsing during prune, we reduce extra allocations and repeated parsing work and may be
    able to eliminate 1–2 re-evaluations.

  Actionable fixes

  - Add a compiled schema cache keyed by (resourceName, method, ReloadId):
      - Use a singleton ConcurrentDictionary<(string, RequestMethod, Guid), JsonSchema>.
      - Populate on first use or at ApiSchema reload time.
      - Replace JsonSchema.FromText(...) with a cache lookup + reuse.
  - Avoid schema text roundtrip:
      - Compile from the in-memory schema representation once, at ApiSchema load time; store compiled JsonSchema instances
        for Insert and Update on ResourceSchema or in a separate cache service.
  - Reuse EvaluationOptions:
      - Mirror ApiSchemaValidator and keep a static readonly EvaluationOptions for document validation.
  - Reduce re-evals:
      - Keep the current logic functional, but by reusing the compiled schema object, the remaining re-evaluations are far
        cheaper.
      - As a follow-up, adjust pruning to mutate JsonNode in-place (no stringify+parse), which may cut the number of
        required re-evaluations or make them cheaper.

  Sketch of a drop-in cache

  public interface ICompiledSchemaCache
  {
      JsonSchema Get(string resourceName, RequestMethod method, Guid reloadId, Func<JsonSchema> factory);
  }

  public sealed class CompiledSchemaCache : ICompiledSchemaCache
  {
      private readonly ConcurrentDictionary<(string, RequestMethod, Guid), JsonSchema> _cache = new();

      public JsonSchema Get(string resource, RequestMethod method, Guid reloadId, Func<JsonSchema> factory)
          => _cache.GetOrAdd((resource, method, reloadId), _ => factory());
  }

  Usage in DocumentValidator (conceptually)

  private static readonly EvaluationOptions EvalOptions = new()
  {
      OutputFormat = OutputFormat.List,
      RequireFormatValidation = true
  };

  private readonly ICompiledSchemaCache _schemaCache;
  private readonly IApiSchemaProvider _apiSchemaProvider;

  private JsonSchema GetCompiledSchema(ResourceSchema resourceSchema, RequestMethod method)
  {
      var reloadId = _apiSchemaProvider.ReloadId;
      var resourceName = resourceSchema.ResourceName.Value;
      return _schemaCache.Get(resourceName, method, reloadId, () =>
      {
          var node = resourceSchema.JsonSchemaForRequestMethod(method);
          var text = JsonSerializer.Serialize(node); // better: compile once at load to avoid stringifying here
          return JsonSchema.FromText(text);
      });
  }

  Severity

  - High for write-heavy scenarios (ingestion, bulk loads) due to CPU and LOH stress.
  - Medium for mixed workloads; still a clear win with minimal risk.

  Follow‑on improvement

  - Precompute compiled Insert/Update schemas at ApiSchema load (and on reload), storing them alongside ResourceSchema. This
    eliminates all per-request compilation and stringification completely.

    Initial GPT5-CODEX - The 15-14 plan
    codex resume 019a5aec-03a8-7921-a925-cfa6dd1ec1e6

    Initial GPT-5 15-53 plan with 15-14 additions (probably best place to continue)
    codex resume 019a5b28-c17e-7861-8a21-2b205fbd3a96
