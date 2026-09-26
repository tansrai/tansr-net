using System.Text.Json;
using Tansr.Sdk.Archive;
using A = Tansr.Sdk.Windows.Storage.ArchiveValidation;

namespace Tansr.Sdk.Windows.Storage;

public sealed partial class SqliteArchiveStore
{
    private static JsonElement RecoveryRequest(JsonElement value) => ArchiveRecoveryContract.Request(value);

    private static JsonElement With(JsonElement value, params (string Name, JsonElement Value)[] replacements) => A.Object(writer =>
    {
        foreach (var property in value.EnumerateObject())
        {
            var replacement = replacements.FirstOrDefault(item => item.Name == property.Name);
            if (replacement.Name == null) property.WriteTo(writer); else A.Property(writer, property.Name, replacement.Value);
        }
    });

    private static JsonElement JsonString(string value) => JsonSerializer.SerializeToElement(value);

    private static int RecoveryMutationDelta(JsonElement intent, string revision)
    {
        var previous = intent.GetProperty("previous");
        int keyDelta = A.Bytes(A.Text(intent.GetProperty("request"))) - A.Bytes(A.Text(previous.GetProperty("request")));
        int revisionDelta = A.Bytes(revision) - A.Bytes(A.String(previous, "expectedRevision"));
        // 原同步文件族：operation key+ACK、移除state.pending、checkpoint key/request及两处revision。
        return keyDelta * 4 + revisionDelta * 3 - A.Bytes(A.Text(previous.GetProperty("request")));
    }

    private static int RecoveryReserve(JsonElement intent)
    {
        const string maximumRevision = "9223372036854775807";
        var next = With(intent.GetProperty("previous"), ("request", intent.GetProperty("request")), ("expectedRevision", JsonString(maximumRevision)));
        var maximum = A.Object(writer =>
        {
            foreach (var property in intent.EnumerateObject()) property.WriteTo(writer);
            A.Property(writer, "next", next); A.Property(writer, "receipt", A.Object(_ => { }));
        });
        return A.Bytes(A.Text(maximum)) + Reserve + Math.Max(0, RecoveryMutationDelta(intent, maximumRevision));
    }

    private bool ReservedRecoveryKey(string request) => _recovery && Number("SELECT count(*) FROM ack_rebases WHERE request=$request OR previous_request=$request", ("$request", request)) != 0;

    private long RecoveryAccount()
    {
        if (!_recovery) return 0;
        A.Need(Number("SELECT count(*) FROM ack_rebases") <= _limits.MaxRecords, "capacity_exceeded");
        return Number("SELECT COALESCE(SUM(length(CAST(request AS BLOB))+length(CAST(previous_request AS BLOB))+length(CAST(intent AS BLOB))+COALESCE(length(CAST(result AS BLOB)),0)+COALESCE(length(CAST(original_receipt AS BLOB)),0)+length(reserve)),0) FROM ack_rebases");
    }

    public Task<JsonElement> PrepareAckRebaseAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        JsonElement fixedRequest = default;
        return Task.FromResult(Run(check =>
        {
            A.Need(_recovery && _syncRole == "source", "invalid_input");
            var state = State(); string key = A.Text(fixedRequest);
            string? existing = (string?)Scalar("SELECT intent FROM ack_rebases WHERE request=$request AND length(CAST(intent AS BLOB))<=1048576", ("$request", key));
            if (existing != null) return RecoveryRequest(A.Parse(existing));
            A.Need(state.Pending != null, "pending_ack");
            A.Need(Number("SELECT count(*) FROM ack_rebases WHERE result IS NULL AND original_receipt IS NULL") == 0, "pending_ack");
            A.Need(key != state.Pending && !ReservedRecoveryKey(key) && Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", key)) == 0, "receipt_mismatch");
            var prior = Operation(state.Pending!); A.Need(prior.Receipt == null && A.String(fixedRequest, "operationEpoch") == A.String(prior.Ack.GetProperty("request"), "operationEpoch"), "receipt_mismatch");
            var intent = RecoveryRequest(A.Object(writer =>
            {
                writer.WriteString("protocol", "sdk2-ext-v1"); writer.WriteString("bindingId", A.String(_identity, "bindingId"));
                A.Property(writer, "previous", prior.Ack); A.Property(writer, "request", fixedRequest);
            }));
            int reserve = RecoveryReserve(intent); A.Need(reserve <= 1048576, "capacity_exceeded");
            Transaction(() =>
            {
                A.Need(State().Pending == state.Pending, "pending_ack");
                Exec("INSERT INTO ack_rebases VALUES($request,$previous,$intent,NULL,NULL,zeroblob($reserve))", ("$request", key), ("$previous", state.Pending!), ("$intent", A.Text(intent)), ("$reserve", reserve));
                Reaccount();
            }, check);
            return intent;
        }, cancellationToken, prepare: () => fixedRequest = A.Copy(request, "RequestIdentity", 4096)));
    }

    public Task<JsonElement?> PendingAckRebaseAsync(CancellationToken cancellationToken = default) => Task.FromResult(Run<JsonElement?>(_ =>
    {
        A.Need(_recovery && _syncRole == "source", "invalid_input");
        string? text = (string?)Scalar("SELECT intent FROM ack_rebases WHERE result IS NULL AND original_receipt IS NULL AND length(CAST(intent AS BLOB))<=1048576");
        if (text == null) return null;
        var intent = RecoveryRequest(A.Parse(text)); string key = A.Text(intent.GetProperty("previous").GetProperty("request"));
        A.Need(State().Pending == key); var operation = Operation(key);
        A.Need(operation.Receipt == null && A.Equal(operation.Ack, intent.GetProperty("previous"))); return intent;
    }, cancellationToken));

    public Task ConfirmAckRebaseAsync(JsonElement receipt, CancellationToken cancellationToken = default)
    {
        JsonElement supplied = default;
        Run<object?>(check =>
        {
            A.Need(_recovery && _syncRole == "source", "invalid_input");
            string key = A.Text(supplied.GetProperty("request")); var row = RecoveryRow(key); A.Need(row != null, "receipt_mismatch");
            var intent = RecoveryRequest(A.Parse(row!.Intent));
            var fixedValue = ArchiveRecoveryContract.Receipt(supplied, intent, _identity.GetProperty("scope"));
            var next = fixedValue.GetProperty("next"); var verifiedReceipt = A.Receipt(_identity, next, fixedValue.GetProperty("receipt"));
            string result = A.Text(fixedValue), receiptText = A.Text(verifiedReceipt);
            if (row.Result != null) { A.Need(row.Result == result, "receipt_mismatch"); return null; }
            A.Need(row.OriginalReceipt == null && State().Pending == row.PreviousRequest, "receipt_mismatch");
            int reserve = RecoveryReserve(intent), delta = RecoveryMutationDelta(intent, A.String(next, "expectedRevision"));
            A.Need(A.Bytes(receiptText) <= Reserve && A.Bytes(result) + delta <= reserve, "capacity_exceeded");
            Transaction(() =>
            {
                A.Need(State().Pending == row.PreviousRequest, "receipt_mismatch"); Exec("PRAGMA defer_foreign_keys=ON");
                Exec("UPDATE operations SET request=$request,ack=$ack,receipt=$receipt,reserve=zeroblob($reserve) WHERE request=$previous AND receipt IS NULL", ("$request", key), ("$ack", A.Text(next)), ("$receipt", receiptText), ("$reserve", Reserve - A.Bytes(receiptText)), ("$previous", row.PreviousRequest));
                string? text = (string?)Scalar("SELECT json FROM checkpoints WHERE request=$request AND length(CAST(json AS BLOB))<=1572864", ("$request", row.PreviousRequest)); A.Need(text != null);
                var prior = A.Parse(text!, maximum: 1572864);
                A.Need(A.Equal(prior.GetProperty("request"), intent.GetProperty("previous").GetProperty("request")) && A.String(prior.GetProperty("binding").GetProperty("operationEpoch"), "id") == A.String(fixedValue.GetProperty("request"), "operationEpoch"), "receipt_mismatch");
                var revision = next.GetProperty("expectedRevision");
                var checkpoint = With(prior, ("request", fixedValue.GetProperty("request")), ("binding", With(prior.GetProperty("binding"), ("revision", revision))), ("status", With(prior.GetProperty("status"), ("revision", revision))));
                Exec("UPDATE checkpoints SET request=$request,json=$json WHERE request=$previous", ("$request", key), ("$json", A.Text(checkpoint, 1572864)), ("$previous", row.PreviousRequest));
                Exec("UPDATE ack_rebases SET result=$result,reserve=zeroblob($reserve) WHERE request=$request", ("$result", result), ("$reserve", reserve - A.Bytes(result) - delta), ("$request", key));
                Exec("UPDATE state SET pending=NULL WHERE id=1"); Reaccount(); AuditRecoveries(State());
            }, check); return null;
        }, cancellationToken, prepare: () => supplied = ArchiveRecoveryContract.ReceiptShape(receipt));
        return Task.CompletedTask;
    }

    private void EnableRecovery(CancellationToken cancellationToken)
    {
        Run<object?>(check =>
        {
            A.Need(!_recovery && _cipher == null && !_historical && _syncRole == "source", "invalid_input");
            Audit(); string previous = _metadata;
            string next = A.Text(With(A.Parse(previous), ("format", JsonString(RecoveryFormat))));
            try
            {
                Transaction(() =>
                {
                    Exec(RecoverySchema); Exec(RecoveryIndex); _metadata = next; _recovery = true;
                    Exec("UPDATE metadata SET json=$json WHERE id=1", ("$json", _metadata)); Reaccount();
                }, check);
            }
            catch { if (!_uncertain) { _metadata = previous; _recovery = false; } throw; }
            return null;
        }, cancellationToken);
    }

    private void AuditRecoveries(StoreState state)
    {
        A.Need(_cipher == null && !_historical && _syncRole == "source");
        A.Need(Number("SELECT count(*) FROM ack_rebases a JOIN ack_rebases b ON a.request=b.previous_request") == 0);
        A.Need(Number("SELECT count(*) FROM ack_rebases WHERE length(CAST(request AS BLOB))>1024 OR length(CAST(previous_request AS BLOB))>1024 OR length(CAST(intent AS BLOB))>1048576 OR length(CAST(result AS BLOB))>1048576 OR length(CAST(original_receipt AS BLOB))>4096 OR length(reserve)>1048576") == 0);
        int pending = 0;
        using var command = Command("SELECT request,previous_request,intent,result,original_receipt,length(reserve) FROM ack_rebases"); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var intent = RecoveryRequest(A.Parse(reader.GetString(2))); string key = reader.GetString(0), oldKey = reader.GetString(1);
            A.Need(key == A.Text(intent.GetProperty("request")) && oldKey == A.Text(intent.GetProperty("previous").GetProperty("request")) && key != oldKey && A.String(intent, "bindingId") == A.String(_identity, "bindingId"));
            string? result = reader.IsDBNull(3) ? null : reader.GetString(3), original = reader.IsDBNull(4) ? null : reader.GetString(4); A.Need(result == null || original == null);
            var fixedResult = result == null ? (JsonElement?)null : ArchiveRecoveryContract.Receipt(A.Parse(result), intent, _identity.GetProperty("scope"));
            A.Need(reader.GetInt64(5) + (result == null ? 0 : A.Bytes(result)) + (original == null ? 0 : A.Bytes(original)) + (fixedResult == null ? 0 : RecoveryMutationDelta(intent, A.String(fixedResult.Value.GetProperty("next"), "expectedRevision"))) == RecoveryReserve(intent));
            if (fixedResult != null)
            {
                var operation = Operation(key); A.Need(A.Equal(operation.Ack, fixedResult.Value.GetProperty("next")) && operation.Receipt == A.Text(fixedResult.Value.GetProperty("receipt")));
                A.Need(Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", oldKey)) == 0);
            }
            else
            {
                var operation = Operation(oldKey); A.Need(A.Equal(operation.Ack, intent.GetProperty("previous")) && Number("SELECT count(*) FROM operations WHERE request=$request", ("$request", key)) == 0);
                if (original == null) { pending++; A.Need(state.Pending == oldKey && operation.Receipt == null); }
                else A.Need(operation.Receipt == original);
            }
        }
        A.Need(pending <= 1);
    }

    private RecoveryEntry? RecoveryRow(string request)
    {
        using var command = Command("SELECT previous_request,intent,result,original_receipt FROM ack_rebases WHERE request=$request AND length(CAST(intent AS BLOB))<=1048576 AND (result IS NULL OR length(CAST(result AS BLOB))<=1048576) AND (original_receipt IS NULL OR length(CAST(original_receipt AS BLOB))<=4096)", ("$request", request)); using var reader = command.ExecuteReader();
        return reader.Read() ? new RecoveryEntry(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }

    private sealed class RecoveryEntry
    {
        internal RecoveryEntry(string previousRequest, string intent, string? result, string? originalReceipt) { PreviousRequest = previousRequest; Intent = intent; Result = result; OriginalReceipt = originalReceipt; }
        internal string PreviousRequest { get; }
        internal string Intent { get; }
        internal string? Result { get; }
        internal string? OriginalReceipt { get; }
    }
}
