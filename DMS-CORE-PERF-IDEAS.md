## performance of the etag logic

### I mean the deepclone -> removal -> serialize -> hash operation. How could that be improved?


• I'm considering ways to simplify the user's request for a conceptual answer, particularly concerning the deep clone operation. Instead of using DeepClone, I can suggest streaming as a more efficient approach. For
  simplification, using JsonDocument or JsonElement with filters is another option. Additionally, I need to clarify how ETag is calculated and the importance of canonical order. So, I'll focus on optimizing the deep clone process
  and explaining the entire pipeline efficiently without modifying the code directly.

  Outlining performance improvements

  I’m thinking about how to structure the answer. First, I’ll outline the current pipeline and address micro performance issues like complexity and allocations. I want to suggest three levels of improvement: One, removing
  DeepClone by working directly off the original JsonObject without mutation. Two, reducing allocations through incremental hashing with a streaming custom writer. Three, avoiding JsonSerializer.Serialize overhead by manual
  traversal. Also, I'll emphasize the need to keep semantics consistent and avoid SHA256 computations over document.ToJsonString() unless certain fields are removed temporarily.

─ Worked for 25s ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

• Right now the ETag pipeline is:

  1. DeepClone the whole JsonNode tree.
  2. Remove _etag, _lastModifiedDate, id from the clone.
  3. JsonSerializer.Serialize the clone to a string.
  4. SHA256.HashData(Encoding.UTF8.GetBytes(json)).
  5. Base64‑encode the hash.

  That’s O(document size) work plus a full clone and a full re‑serialize, with multiple large allocations (clone tree, JSON string, UTF‑8 byte array). For big docs and high write rates, that can become noticeable CPU/GC overhead.

  Ways to improve it, while keeping the same semantics:

  1. Avoid DeepClone by not mutating the original

  Today you only remove top‑level fields. You don’t actually need a deep clone for that.

  Instead of:

  var parsedBody = document.DeepClone() as JsonObject;
  parsedBody!.Remove("_etag");
  parsedBody!.Remove("_lastModifiedDate");
  parsedBody!.Remove("id");
  var parsedJson = JsonSerializer.Serialize(parsedBody);

  you can:

  var obj = (JsonObject)document;
  using var buffer = new ArrayBufferWriter<byte>();
  using var writer = new Utf8JsonWriter(buffer);

  WriteFilteredObject(obj, writer);
  writer.Flush();

  var hash = SHA256.HashData(buffer.WrittenSpan);
  return Convert.ToBase64String(hash);

  // Only skip top‑level metadata keys; preserve order.
  static void WriteFilteredObject(JsonObject obj, Utf8JsonWriter writer)
  {
      writer.WriteStartObject();
      foreach (var (name, value) in obj)
      {
          if (name is "_etag" or "_lastModifiedDate" or "id") continue;
          writer.WritePropertyName(name);
          value!.WriteTo(writer); // no clone; just stream through
      }
      writer.WriteEndObject();
  }

  This:

  - Eliminates DeepClone.
  - Eliminates the intermediate JSON string and the extra UTF‑8 encode step.
  - Still preserves original property order, so ETag semantics stay the same.

  2. Stream directly into the hash (no buffer at all)

  If you want to push further, you can avoid even the ArrayBufferWriter<byte> by implementing an IBufferWriter<byte> that feeds bytes directly into a HashAlgorithm / IncrementalHash on Advance(...). Then:

  - Utf8JsonWriter writes straight into the hash.
  - At the end you just call hash.GetHashAndReset() and Base64‑encode.

  That’s minimal allocations and still O(document size).

  3. Optional semantic improvements

  If you want ETags to be insensitive to property ordering, you could sort object properties by name before writing them in WriteFilteredObject. That’s a behavioral change (could reduce spurious ETag changes) but costs an extra
  sort per object.

  ———

  So the main practical win is: stop cloning and serializing a second time. Traverse once, write only the fields that matter into a Utf8JsonWriter, and hash those bytes.

  
