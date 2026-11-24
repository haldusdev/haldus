using System.Text.RegularExpressions;
using Haldus.Generators.Infrastructure;
using Haldus.Generators.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Haldus.Generators.Parsing;

/// <summary>
/// Zincirdeki yaşam döngüsü bildirimlerini okur: durum makinesi (<c>Workflow</c>, <c>EditableIn</c>,
/// <c>DeletableIn</c>), numaralama (<c>Numbered</c>) ve son kullanma (<c>Expiring</c>). Bunların argümanları
/// derleme zamanında okunabilen sabitler olmalıdır.
/// </summary>
internal static class LifecycleReader
{
    private const string DateTimeOffset = "global::System.DateTimeOffset";

    private static readonly Regex PatternPart = new(@"\{(?<token>[^{}]*)\}", RegexOptions.CultureInvariant);
    private static readonly Regex SequencePart = new(@"^seq(:(?<width>[0-9]{1,2}))?$", RegexOptions.CultureInvariant);

    internal sealed class Result
    {
        public WorkflowModel? Workflow { get; set; }

        public NumberingModel? Numbering { get; set; }

        public ExpiryModel? Expiry { get; set; }

        public BatchModel? Batch { get; set; }

        public SecretModel? Secret { get; set; }

        public TokenModel? Token { get; set; }

        public bool TokenOnly { get; set; }

        public ApprovalModel? Approval { get; set; }

        public long? RetentionTicks { get; set; }

        public List<AxisModel> Axes { get; } = [];

        /// <summary><c>Buckets(...)</c> ile sayılan kova adları.</summary>
        public List<string> Buckets { get; } = [];

        public bool AllowNegative { get; set; }

        public ChangeSetModel? ChangeSets { get; set; }

        /// <summary>Kaydın ters çevrilebildiği durumlar; durum makinesi yoksa <c>null</c>.</summary>
        public EquatableArray<string>? ReversibleIn { get; set; }

        /// <summary>Kaydın yerine geçilebildiği durumlar; durum makinesi yoksa <c>null</c>.</summary>
        public EquatableArray<string>? SupersedableIn { get; set; }

        /// <summary>Bildirimlerin entity'ye eklediği, istek gövdesinden yazılamayan alanlar.</summary>
        public List<TraitMember> Members { get; } = [];
    }

    public static Result Read(
        SemanticModel semanticModel,
        InvocationExpressionSyntax mapCall,
        string entityName,
        List<DiagnosticInfo> diagnostics,
        CancellationToken cancellationToken)
    {
        var result = new Result();
        EquatableArray<string>? editableIn = null;
        EquatableArray<string>? deletableIn = null;
        StateReference? assignOn = null;
        LocationInfo? ruleLocation = null;
        LocationInfo? numberingLocation = null;
        (SimpleNameSyntax Name, SeparatedSyntaxList<ArgumentSyntax> Arguments)? reversible = null;
        (SimpleNameSyntax Name, SeparatedSyntaxList<ArgumentSyntax> Arguments)? supersedes = null;
        (SimpleNameSyntax Name, SeparatedSyntaxList<ArgumentSyntax> Arguments)? changeSets = null;
        var rotatable = false;
        long? overlap = null;
        SimpleNameSyntax? document = null;

        void Invalid(SyntaxNode node, string message) => diagnostics.Add(new DiagnosticInfo(
            "HALDUS010", LocationInfo.From(node.GetLocation()), new EquatableArray<string>([message])));

        foreach (var (name, call) in ChainReader.Nodes(mapCall))
        {
            var arguments = call.ArgumentList.Arguments;

            switch (name.Identifier.ValueText)
            {
                case "Workflow":
                    result.Workflow = ReadWorkflow(semanticModel, name, arguments, entityName, Invalid, cancellationToken);
                    break;

                case "Axis":
                    if (ReadWorkflow(semanticModel, name, arguments, entityName, Invalid, cancellationToken) is { } axis)
                    {
                        var property = axis.StatusType.Substring(axis.StatusType.LastIndexOfAny(['.', ':']) + 1);

                        if (result.Axes.Any(existing => existing.StatusType == axis.StatusType))
                        {
                            Invalid(name, $"'{entityName}' already has an axis of '{property}'; each axis needs a status type of its own");
                        }
                        else
                        {
                            result.Axes.Add(new AxisModel(axis.StatusType, property, axis.Initial));
                        }
                    }

                    break;

                case "EditableIn":
                    ruleLocation ??= LocationInfo.From(name.GetLocation());
                    editableIn = ReadStates(semanticModel, name, arguments, result.Workflow, Invalid, cancellationToken);
                    break;

                case "DeletableIn":
                    ruleLocation ??= LocationInfo.From(name.GetLocation());
                    deletableIn = ReadStates(semanticModel, name, arguments, result.Workflow, Invalid, cancellationToken);
                    break;

                case "Numbered":
                    numberingLocation = LocationInfo.From(name.GetLocation());
                    result.Numbering = ReadNumbering(semanticModel, name, arguments, out assignOn, Invalid, cancellationToken);
                    break;

                case "Expiring":
                    result.Expiry = ReadExpiry(semanticModel, name, arguments, Invalid, cancellationToken);
                    break;

                case "Batch":
                    result.Batch = ReadBatch(semanticModel, name, arguments, Invalid, cancellationToken);
                    break;

                case "SecretOnce":
                    result.Secret = ReadSecret(semanticModel, name, arguments, Invalid, cancellationToken);
                    break;

                case "Rotatable":
                    rotatable = true;

                    foreach (var argument in arguments.Where(argument => argument.Expression.ToString() != "null"))
                    {
                        if (TimeSpanTicks(semanticModel, argument.Expression, cancellationToken) is { } ticks and > 0)
                        {
                            overlap = ticks;
                        }
                        else
                        {
                            Invalid(argument, "The overlap of Rotatable must be readable at compile time and longer than zero; write it as TimeSpan.FromHours(24) with constant arguments");
                        }
                    }

                    break;

                case "TokenAccess":
                    result.Token = ReadToken(semanticModel, name, arguments, Invalid, cancellationToken);
                    break;

                case "TokenOnly":
                    result.TokenOnly = true;
                    break;

                // Durumları, durum makinesi zincirin neresinde bildirilmiş olursa olsun doğrulanabilsin diye
                // döngüden sonra okunur.
                case "Reversible":
                    reversible = (name, arguments);
                    break;

                case "Supersedes":
                    supersedes = (name, arguments);
                    break;

                case "ChangeSets":
                    changeSets = (name, arguments);
                    break;

                case "Document":
                    document = name;
                    break;

                case "Approvable":
                    result.Approval = ReadApproval(semanticModel, arguments, Invalid, cancellationToken);
                    break;

                case "AllowNegative":
                    result.AllowNegative = true;
                    break;

                case "Buckets":
                    foreach (var argument in arguments)
                    {
                        // Kova adı isteklerde ve defterde geçer, alan adına da çevrilir; sabit ve sade olmalıdır.
                        if (semanticModel.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true, Value: string bucket }
                            && bucket.Length is > 0 and <= 40
                            && bucket[0] is >= 'a' and <= 'z'
                            && bucket[bucket.Length - 1] != '-'
                            && bucket.IndexOf("--", StringComparison.Ordinal) < 0
                            && bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                            && !result.Buckets.Contains(bucket))
                        {
                            result.Buckets.Add(bucket);
                        }
                        else
                        {
                            Invalid(argument, "A bucket is named with a constant made of lowercase words separated by '-', for example Buckets(\"available\", \"on-hold\"), and each name is used once");
                        }
                    }

                    break;

                case "Retention":
                    if (arguments.Count == 1 && TimeSpanTicks(semanticModel, arguments[0].Expression, cancellationToken) is { } period and > 0)
                    {
                        result.RetentionTicks = period;
                    }
                    else
                    {
                        Invalid(name, "The retention period must be readable at compile time and longer than zero; write it as TimeSpan.FromDays(90) with constant arguments");
                    }

                    break;
            }
        }

        if (document is not null)
        {
            if (result.Workflow is not null || ruleLocation is not null)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS007",
                    LocationInfo.From(document.GetLocation()),
                    new EquatableArray<string>(["Document() fixes the states of the record and their rules; remove Workflow, EditableIn and DeletableIn"])));
            }
            else
            {
                // Belge, durumları ve kuralları sabit bir durum makinesidir: taslak düzenlenir ve silinir.
                result.Workflow = new WorkflowModel(DocumentStatus, "Draft", EditableIn: null, DeletableIn: null);
                editableIn = new EquatableArray<string>(["Draft"]);
                deletableIn = new EquatableArray<string>(["Draft"]);
            }
        }

        if (result.Secret is { } secret)
        {
            result.Secret = secret = secret with { Rotatable = rotatable, OverlapTicks = rotatable ? overlap : null };

            // Sırrın kendisi saklanmaz; özeti aranır, son karakterleri listede gösterilir.
            result.Members.Add(new TraitMember("SecretHash", "string", InResponse: false, Search: null));
            result.Members.Add(new TraitMember("SecretHint", "string", InResponse: true, Search: null));

            if (secret.Recoverable)
            {
                result.Members.Add(new TraitMember("SecretCipher", "string", InResponse: false, Search: null));
            }

            if (secret.OverlapTicks is not null)
            {
                result.Members.Add(new TraitMember("PreviousSecretHash", "string?", InResponse: false, Search: null));
                result.Members.Add(new TraitMember("PreviousSecretExpiresAt", DateTimeOffset + "?", InResponse: false, Search: null));
            }
        }

        if (result.Token is not null)
        {
            // Zincirde hangisi önce yazılırsa yazılsın, kısıt token'ın bir özelliğidir.
            result.Token = result.Token with { Only = result.TokenOnly };
            result.Members.Add(new TraitMember("TokenHash", "string", InResponse: false, Search: null));
        }

        foreach (var axis in result.Axes)
        {
            if (axis.StatusType == result.Workflow?.StatusType || axis.Property == "Status")
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS007",
                    LocationInfo.From(mapCall.GetLocation()),
                    new EquatableArray<string>([$"Axis<{axis.Property}>() repeats the status of '{entityName}'; an axis needs a status type of its own, different from the one given to Workflow"])));
                continue;
            }

            // Eksen de asıl durum gibi yanıtta adıyla görünür ve yalnızca geçişlerle değişir.
            result.Members.Add(new TraitMember(
                axis.Property,
                axis.StatusType,
                InResponse: true,
                new SearchField(axis.Property, SearchKind.Equality, axis.StatusType, IsNullable: false),
                ResponseAttribute: "global::System.Text.Json.Serialization.JsonConverter(typeof("
                    + $"global::System.Text.Json.Serialization.JsonStringEnumConverter<{axis.StatusType}>))"));
        }

        result.ReversibleIn = ReadChainStates(semanticModel, reversible, result.Workflow, "reversed", Invalid, cancellationToken);
        result.SupersedableIn = ReadChainStates(semanticModel, supersedes, result.Workflow, "superseded", Invalid, cancellationToken);

        if (changeSets is not null)
        {
            // Küme, kaydın düzenlenemediği durumlarda da çalışan bir yazmadır; durumları açıkça sayılmalıdır.
            result.ChangeSets = new ChangeSetModel(
                ReadChainStates(semanticModel, changeSets, result.Workflow, "edited through a change set", Invalid, cancellationToken));
        }

        if (result.Workflow is { } workflow)
        {
            result.Workflow = workflow with { EditableIn = editableIn, DeletableIn = deletableIn };

            result.Members.Add(new TraitMember(
                "Status",
                workflow.StatusType,
                InResponse: true,
                new SearchField("Status", SearchKind.Equality, workflow.StatusType, IsNullable: false),

                // Durum yanıtta sayı olarak değil adıyla görünür; uygulamanın genel JSON ayarına bağlı kalmaz.
                ResponseAttribute: "global::System.Text.Json.Serialization.JsonConverter(typeof("
                    + $"global::System.Text.Json.Serialization.JsonStringEnumConverter<{workflow.StatusType}>))"));
        }
        else if (ruleLocation is not null && document is null)
        {
            diagnostics.Add(new DiagnosticInfo(
                "HALDUS007",
                ruleLocation,
                new EquatableArray<string>(["EditableIn and DeletableIn need a state machine; declare Workflow<TStatus>() before them"])));
        }

        if (result.Numbering is { } numbering)
        {
            if (assignOn is not null && result.Workflow is null)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS007",
                    numberingLocation!,
                    new EquatableArray<string>(["Numbered(assignOn: ...) needs a state machine; declare Workflow<TStatus>() on the resource"])));
            }
            else if (assignOn is not null && assignOn.Type != result.Workflow!.StatusType)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "HALDUS010",
                    numberingLocation!,
                    new EquatableArray<string>([$"'{assignOn.Name}' is not a state of '{entityName}'; assignOn must be a member of its status type"])));
            }

            // Numara bir duruma geçerken atanıyorsa o ana kadar boştur.
            var assignedLater = numbering.AssignOn is not null;
            result.Members.Add(new TraitMember(
                "Number",
                assignedLater ? "string?" : "string",
                InResponse: true,
                new SearchField("Number", SearchKind.String, "string", IsNullable: assignedLater)));
        }

        if (result.Expiry is { LifetimeTicks: not null })
        {
            // Ömrü sunucu veriyorsa son kullanma zamanı istek gövdesinden yazılamaz.
            result.Members.Add(new TraitMember(
                "ExpiresAt",
                DateTimeOffset + "?",
                InResponse: true,
                new SearchField("ExpiresAt", SearchKind.Range, DateTimeOffset, IsNullable: true)));
        }

        return result;
    }

    /// <summary>
    /// Bir işin Haldus'a ait alanları (<c>IHaldusJob</c>). İstek gövdesinden yazılamazlar; sayaç ve iptal
    /// bayrağı yanıtta da görünmez.
    /// </summary>
    public static IEnumerable<TraitMember> JobMembers()
    {
        const string JobStatus = "global::Haldus.JobStatus";

        yield return new TraitMember(
            "Status",
            JobStatus,
            InResponse: true,
            new SearchField("Status", SearchKind.Equality, JobStatus, IsNullable: false),
            ResponseAttribute: "global::System.Text.Json.Serialization.JsonConverter(typeof("
                + $"global::System.Text.Json.Serialization.JsonStringEnumConverter<{JobStatus}>))");
        yield return new TraitMember("Progress", "int", InResponse: true, Search: null);
        yield return new TraitMember("Error", "string?", InResponse: true, Search: null);
        yield return new TraitMember(
            "CreatedAt", DateTimeOffset, InResponse: true, new SearchField("CreatedAt", SearchKind.Range, DateTimeOffset, IsNullable: false));
        yield return new TraitMember(
            "StartedAt", DateTimeOffset + "?", InResponse: true, new SearchField("StartedAt", SearchKind.Range, DateTimeOffset, IsNullable: true));
        yield return new TraitMember(
            "CompletedAt", DateTimeOffset + "?", InResponse: true, new SearchField("CompletedAt", SearchKind.Range, DateTimeOffset, IsNullable: true));
        yield return new TraitMember("Attempts", "int", InResponse: false, Search: null);
        yield return new TraitMember("CancelRequested", "bool", InResponse: false, Search: null);
        yield return new TraitMember("Culture", "string?", InResponse: false, Search: null);
    }

    /// <summary>Son kullanma zamanını istemcinin verdiği modda üretilen, yazılabilir alan.</summary>
    public static PropertyModel ClientExpiry() => new(
        "ExpiresAt",
        DateTimeOffset + "?",
        NullableType: DateTimeOffset + "?",
        IsWritable: true,
        IsNonNullableReference: false,
        IsNonNullableValue: false,
        SearchKind.Range,
        DateTimeOffset,
        EquatableArray<AttributeModel>.Empty,
        IsGenerated: true);

    public const string DocumentStatus = "global::Haldus.DocumentStatus";

    private const string DateOnly = "global::System.DateOnly";

    private const string FileStatus = "global::Haldus.FileStatus";

    /// <summary>Bir dosyanın Haldus'a ait alanları (<c>IHaldusFile</c>): içerik yüklenirken yazılırlar.</summary>
    public static IEnumerable<TraitMember> FileMembers()
    {
        yield return new TraitMember("ContentType", "string?", InResponse: true, new SearchField("ContentType", SearchKind.String, "string", IsNullable: true));
        yield return new TraitMember("Size", "long", InResponse: true, new SearchField("Size", SearchKind.Range, "long", IsNullable: false));
        yield return new TraitMember("Checksum", "string?", InResponse: true, Search: null);
        yield return new TraitMember(
            "Status",
            FileStatus,
            InResponse: true,
            new SearchField("Status", SearchKind.Equality, FileStatus, IsNullable: false),
            ResponseAttribute: $"global::System.Text.Json.Serialization.JsonConverter(typeof(global::System.Text.Json.Serialization.JsonStringEnumConverter<{FileStatus}>))");
        yield return new TraitMember("UploadedAt", DateTimeOffset + "?", InResponse: true, new SearchField("UploadedAt", SearchKind.Range, DateTimeOffset, IsNullable: true));

        // Deponun anahtarı dışarıya gösterilmez.
        yield return new TraitMember("StorageKey", "string?", InResponse: false, Search: null);
    }

    /// <summary>Dışa aktarma işinin çıktıları (<c>IHaldusExportJob</c>).</summary>
    public static IEnumerable<TraitMember> ExportMembers()
    {
        yield return new TraitMember("Rows", "int", InResponse: true, Search: null);
        yield return new TraitMember("Size", "long", InResponse: true, Search: null);
        yield return new TraitMember("FileName", "string?", InResponse: true, Search: null);
        yield return new TraitMember("StorageKey", "string?", InResponse: false, Search: null);
    }

    /// <summary>İçe aktarma işinin çıktıları (<c>IHaldusImportJob</c>).</summary>
    public static IEnumerable<TraitMember> ImportMembers()
    {
        yield return new TraitMember("Imported", "int", InResponse: true, Search: null);
        yield return new TraitMember("Failed", "int", InResponse: true, Search: null);
        yield return new TraitMember("Errors", "string?", InResponse: true, Search: null);
    }

    /// <summary>Aktarma işinin girdileri: biçim ve, içe aktarmada, yanıtta gösterilmeyen içerik.</summary>
    public static IEnumerable<PropertyModel> TransferInputs(bool export)
    {
        // Biçim isteğe bağlıdır; verilmezse CSV sayılır.
        yield return new PropertyModel(
            "Format",
            "string?",
            NullableType: "string?",
            IsWritable: true,
            IsNonNullableReference: false,
            IsNonNullableValue: false,
            SearchKind.String,
            "string",
            new EquatableArray<AttributeModel>([new AttributeModel("global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(10)", IsRequired: false)]),
            IsGenerated: true);

        if (!export)
        {
            yield return new PropertyModel(
                "Content",
                "string",
                NullableType: "string?",
                IsWritable: true,
                IsNonNullableReference: true,
                IsNonNullableValue: false,
                SearchKind.String,
                "string",
                new EquatableArray<AttributeModel>(
                [
                    new AttributeModel("global::System.ComponentModel.DataAnnotations.RequiredAttribute", IsRequired: true),
                    new AttributeModel("global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(5000000)", IsRequired: false),
                ]),
                IsGenerated: true)
            {
                IsInputOnly = true,
            };
        }
    }

    /// <summary>Dosyanın adı: istemcinin yazdığı sıradan bir alandır.</summary>
    public static PropertyModel FileName() => new(
        "FileName",
        "string",
        NullableType: "string?",
        IsWritable: true,
        IsNonNullableReference: true,
        IsNonNullableValue: false,
        SearchKind.String,
        "string",
        new EquatableArray<AttributeModel>(
        [
            new AttributeModel("global::System.ComponentModel.DataAnnotations.RequiredAttribute", IsRequired: true),
            new AttributeModel("global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(255)", IsRequired: false),
        ]),
        IsGenerated: true);

    /// <summary><c>MapFile</c> çağrısının argümanları: en büyük boyut ve kabul edilen türler; ikisi de sabit olmalıdır.</summary>
    public static FileModel? ReadFile(
        SemanticModel semanticModel,
        InvocationExpressionSyntax mapCall,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var maxBytes = 10L * 1024 * 1024;
        var contentTypes = new List<string>();

        foreach (var argument in mapCall.ArgumentList.Arguments)
        {
            var constant = semanticModel.GetConstantValue(argument.Expression, cancellationToken);

            if (constant is { HasValue: true, Value: int or long } && Convert.ToInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture) is var size)
            {
                if (size is < 1 or > 1024L * 1024 * 1024)
                {
                    invalid(argument, "The largest content of a file must be between 1 byte and 1 GB");
                    return null;
                }

                maxBytes = size;
            }
            else if (constant is { HasValue: true, Value: string type } && type.Split('/') is { Length: 2 } parts && parts.All(part => part.Length > 0))
            {
                contentTypes.Add(type.ToLowerInvariant());
            }
            else
            {
                invalid(argument, "The arguments of MapFile must be constants: the largest content in bytes, then the accepted content types such as \"image/png\" or \"image/*\"");
                return null;
            }
        }

        return new FileModel(maxBytes, contentTypes.Distinct().ToEquatableArray());
    }

    /// <summary>Yinelenen kaydın (<c>.Recurring()</c>) istemcinin yazdığı alanları: ilk oluş ve kural.</summary>
    public static IEnumerable<PropertyModel> RecurrenceFields()
    {
        const string Frequency = "global::Haldus.RecurrenceFrequency";
        const string Range = "global::System.ComponentModel.DataAnnotations.RangeAttribute(1, 1000)";

        PropertyModel Field(string name, string type, bool required, string valueType, SearchKind kind, params string[] attributes) => new(
            name,
            required ? type : type + "?",
            NullableType: type + "?",
            IsWritable: true,
            IsNonNullableReference: false,
            IsNonNullableValue: required,
            kind,
            valueType,
            attributes.Select(attribute => new AttributeModel(attribute, IsRequired: false)).ToEquatableArray(),
            IsGenerated: true);

        yield return Field("StartsAt", DateTimeOffset, required: true, DateTimeOffset, SearchKind.Range);
        yield return Field("RecurrenceFrequency", Frequency, required: true, Frequency, SearchKind.Equality);
        yield return Field("RecurrenceInterval", "int", required: false, "int", SearchKind.Range, Range);
        yield return Field("RecurrenceUntil", DateTimeOffset, required: false, DateTimeOffset, SearchKind.Range);
        yield return Field("RecurrenceCount", "int", required: false, "int", SearchKind.Range, Range);
    }

    /// <summary>Dönemli kaydın (<c>.EffectiveDated()</c>) istemcinin yazdığı iki alanı.</summary>
    public static IEnumerable<PropertyModel> EffectivePeriod()
    {
        yield return new PropertyModel(
            "EffectiveFrom",
            DateOnly,
            NullableType: DateOnly + "?",
            IsWritable: true,
            IsNonNullableReference: false,
            IsNonNullableValue: true,
            SearchKind.Range,
            DateOnly,
            EquatableArray<AttributeModel>.Empty,
            IsGenerated: true);

        yield return new PropertyModel(
            "EffectiveTo",
            DateOnly + "?",
            NullableType: DateOnly + "?",
            IsWritable: true,
            IsNonNullableReference: false,
            IsNonNullableValue: false,
            SearchKind.Range,
            DateOnly,
            EquatableArray<AttributeModel>.Empty,
            IsGenerated: true);
    }

    private static ApprovalModel? ReadApproval(
        SemanticModel semanticModel,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var levels = 1;
        var requiredFor = new List<string>();

        foreach (var argument in arguments)
        {
            var named = argument.NameColon?.Name.Identifier.ValueText;
            var constant = semanticModel.GetConstantValue(argument.Expression, cancellationToken);

            if (named != "requiredFor" && constant is { HasValue: true, Value: int count })
            {
                if (count is < 1 or > 10)
                {
                    invalid(argument, "Approvable needs between 1 and 10 levels");
                    return null;
                }

                levels = count;
                continue;
            }

            // Adlar tek tek (params) veya bir koleksiyon ifadesiyle verilebilir.
            var names = argument.Expression is CollectionExpressionSyntax collection
                ? collection.Elements.Select(element => (element as ExpressionElementSyntax)?.Expression)
                : [argument.Expression];

            foreach (var name in names)
            {
                if (named != "levels"
                    && name is not null
                    && semanticModel.GetConstantValue(name, cancellationToken) is { HasValue: true, Value: string route })
                {
                    requiredFor.Add(route);
                }
                else
                {
                    invalid(argument, "The arguments of Approvable must be constants: the number of levels, then the names of the transitions that need an approved record");
                    return null;
                }
            }
        }

        return new ApprovalModel(levels, requiredFor.Distinct().ToEquatableArray());
    }

    /// <summary>İfade bir enum üyesiyse onu verir: <c>OrderStatus.Draft</c>.</summary>
    public static StateReference? StateOf(SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken) =>
        semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } field
            ? new StateReference(field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), field.Name)
            : null;

    internal static WorkflowModel? ReadWorkflow(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string entityName,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var initial = arguments.Count > 0 ? StateOf(semanticModel, arguments[0].Expression, cancellationToken) : null;

        // Durum tipi ya açıkça yazılır (Workflow<OrderStatus>()) ya da başlangıç durumundan çıkarsanır.
        var statusType = name is GenericNameSyntax { TypeArgumentList.Arguments: { Count: 1 } typeArguments }
            ? semanticModel.GetTypeInfo(typeArguments[0], cancellationToken).Type as INamedTypeSymbol
            : arguments.Count > 0
                ? semanticModel.GetTypeInfo(arguments[0].Expression, cancellationToken).Type as INamedTypeSymbol
                : null;

        if (statusType is not { TypeKind: TypeKind.Enum })
        {
            invalid(name, $"The status type of '{entityName}' must be an enum: Workflow<OrderStatus>(initial: OrderStatus.Draft)");
            return null;
        }

        if (arguments.Count > 0 && initial is null)
        {
            invalid(arguments[0], "The initial state must be written as an enum member, for example Workflow<OrderStatus>(initial: OrderStatus.Draft)");
            return null;
        }

        var members = statusType.GetMembers().OfType<IFieldSymbol>().Where(field => field.HasConstantValue).ToList();
        var initialName = initial?.Name
            ?? members.FirstOrDefault(field => Convert.ToInt64(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == 0)?.Name;

        if (initialName is null)
        {
            invalid(name, $"'{statusType.Name}' has no member with the value 0; pass the initial state explicitly: Workflow<{statusType.Name}>(initial: ...)");
            return null;
        }

        return new WorkflowModel(
            statusType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            initialName,
            EditableIn: null,
            DeletableIn: null);
    }

    private static EquatableArray<string>? ReadStates(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        WorkflowModel? workflow,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var states = new List<string>();

        foreach (var argument in arguments)
        {
            if (StateOf(semanticModel, argument.Expression, cancellationToken) is not { } state)
            {
                invalid(argument, $"{name.Identifier.ValueText} takes states written as enum members, for example {name.Identifier.ValueText}(OrderStatus.Draft)");
            }
            else if (workflow is not null && state.Type != workflow.StatusType)
            {
                invalid(argument, $"'{state.Name}' is not a member of the status type declared with Workflow<TStatus>()");
            }
            else if (!states.Contains(state.Name))
            {
                states.Add(state.Name);
            }
        }

        if (arguments.Count == 0)
        {
            invalid(name, $"{name.Identifier.ValueText} needs at least one state");
        }

        return states.ToEquatableArray();
    }

    private static SecretModel? ReadSecret(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        string? prefix = null;
        var recoverable = false;

        foreach (var argument in arguments)
        {
            switch (semanticModel.GetConstantValue(argument.Expression, cancellationToken))
            {
                case { HasValue: true, Value: string text }:
                    prefix = text;
                    break;
                case { HasValue: true, Value: bool flag }:
                    recoverable = flag;
                    break;
                default:
                    invalid(argument, "The arguments of SecretOnce must be constants: a string for the prefix and true or false for recoverable");
                    return null;
            }
        }

        if (prefix is null || !MapCallParser.IsKeyPrefix(prefix))
        {
            invalid(name, PrefixRule("SecretOnce", "sk"));
            return null;
        }

        return new SecretModel(prefix, recoverable, Rotatable: false, OverlapTicks: null);
    }

    private static TokenModel? ReadToken(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var texts = new List<string>();

        foreach (var argument in arguments)
        {
            if (semanticModel.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true, Value: string text })
            {
                texts.Add(text);
            }
            else
            {
                // Hangi yazmaların oturumsuz yapılabildiği derleme zamanında bilinmelidir.
                invalid(argument, "The arguments of TokenAccess must be string constants: the prefix, then the names of the transitions and actions the token may run");
                return null;
            }
        }

        if (texts.Count == 0 || !MapCallParser.IsKeyPrefix(texts[0]))
        {
            invalid(name, PrefixRule("TokenAccess", "inv"));
            return null;
        }

        return new TokenModel(texts[0], texts.Skip(1).Distinct().ToEquatableArray());
    }

    private static string PrefixRule(string declaration, string example) =>
        $"{declaration} needs a prefix: it starts with a lowercase letter, contains only lowercase letters and digits, "
        + $"and is at most 12 characters long, for example {declaration}(prefix: \"{example}\")";

    /// <summary>
    /// Ters çevirme ve yerine geçme, kaydın düzenlenemediği durumlarda da çalışan yazmalardır. Kaynağın bir durum
    /// makinesi varsa hangi durumlarda yapılabilecekleri açıkça sayılmalıdır; sayılmazsa bildirim reddedilir, çünkü
    /// "her durumda" varsayımı durum kurallarını sessizce atlamak olurdu.
    /// </summary>
    private static EquatableArray<string>? ReadChainStates(
        SemanticModel semanticModel,
        (SimpleNameSyntax Name, SeparatedSyntaxList<ArgumentSyntax> Arguments)? call,
        WorkflowModel? workflow,
        string verb,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        if (call is not { } declaration)
        {
            return null;
        }

        var name = declaration.Name.Identifier.ValueText;

        if (workflow is null)
        {
            if (declaration.Arguments.Count > 0)
            {
                invalid(declaration.Name, $"{name} can list states only on a resource with a state machine; declare Workflow<TStatus>() or remove the states");
            }

            return null;
        }

        if (declaration.Arguments.Count == 0)
        {
            invalid(
                declaration.Name,
                $"The resource has a state machine, so {name} must list the states in which a record may be {verb}, for example {name}(Status.Posted)");
            return null;
        }

        return ReadStates(semanticModel, declaration.Name, declaration.Arguments, workflow, invalid, cancellationToken);
    }

    private static NumberingModel? ReadNumbering(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        out StateReference? assignOn,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var pattern = "{seq:6}";
        var gapless = true;
        assignOn = null;

        foreach (var argument in arguments)
        {
            var constant = semanticModel.GetConstantValue(argument.Expression, cancellationToken);

            if (constant is { HasValue: true, Value: string text })
            {
                pattern = text;
            }
            else if (constant is { HasValue: true, Value: bool flag })
            {
                gapless = flag;
            }
            else if (StateOf(semanticModel, argument.Expression, cancellationToken) is { } state)
            {
                assignOn = state;
            }
            else if (!argument.Expression.IsKind(SyntaxKind.NullLiteralExpression))
            {
                invalid(argument, "The arguments of Numbered must be constants: a pattern such as \"ORD-{yyyy}-{seq:6}\", an enum member for assignOn and true or false for gapless");
                return null;
            }
        }

        if (PatternProblem(pattern) is { } problem)
        {
            invalid(name, problem);
            return null;
        }

        return new NumberingModel(pattern, gapless, assignOn?.Name);
    }

    /// <summary>Çalışma zamanındaki <c>NumberFormat</c> ile aynı kuralları derleme zamanında uygular.</summary>
    private static string? PatternProblem(string pattern)
    {
        var sequences = 0;

        foreach (Match part in PatternPart.Matches(pattern))
        {
            var token = part.Groups["token"].Value;
            var sequence = SequencePart.Match(token);

            if (sequence.Success)
            {
                sequences++;

                if (sequence.Groups["width"].Success && int.Parse(sequence.Groups["width"].Value, System.Globalization.CultureInfo.InvariantCulture) is 0 or > 18)
                {
                    return $"The number pattern '{pattern}' pads {{seq}} to an unsupported width; use 1 to 18 digits";
                }
            }
            else if (token is not ("yyyy" or "yy" or "MM" or "dd"))
            {
                return $"The number pattern '{pattern}' has an unknown part '{{{token}}}'; supported parts are {{seq}}, {{seq:N}}, {{yyyy}}, {{yy}}, {{MM}} and {{dd}}";
            }
        }

        if (PatternPart.Replace(pattern, string.Empty).IndexOfAny(['{', '}']) >= 0)
        {
            return $"The number pattern '{pattern}' has an unbalanced brace";
        }

        return sequences == 1 ? null : $"The number pattern '{pattern}' must contain {{seq}} exactly once";
    }

    private static BatchModel? ReadBatch(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        var atomic = true;
        var maxItems = 100;

        foreach (var argument in arguments)
        {
            switch (semanticModel.GetConstantValue(argument.Expression, cancellationToken))
            {
                case { HasValue: true, Value: bool flag }:
                    atomic = flag;
                    break;
                case { HasValue: true, Value: int count }:
                    maxItems = count;
                    break;
                default:
                    invalid(argument, "The arguments of Batch must be constants: true or false for atomic and a number for maxItems");
                    return null;
            }
        }

        if (maxItems is < 1 or > 10_000)
        {
            invalid(name, "The maxItems of Batch must be between 1 and 10000");
            return null;
        }

        return new BatchModel(atomic, maxItems);
    }

    private static ExpiryModel? ReadExpiry(
        SemanticModel semanticModel,
        SimpleNameSyntax name,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        Action<SyntaxNode, string> invalid,
        CancellationToken cancellationToken)
    {
        long? lifetime = null;
        var sliding = false;

        foreach (var argument in arguments)
        {
            if (semanticModel.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true, Value: bool flag })
            {
                sliding = flag;
            }
            else if (TimeSpanTicks(semanticModel, argument.Expression, cancellationToken) is { } ticks)
            {
                lifetime = ticks;
            }
            else
            {
                invalid(argument, "The lifetime of Expiring must be readable at compile time; write it as TimeSpan.FromMinutes(15), TimeSpan.FromHours(1) or TimeSpan.FromDays(7) with constant arguments");
                return null;
            }
        }

        if (lifetime is <= 0)
        {
            invalid(name, "The lifetime of Expiring must be longer than zero");
            return null;
        }

        return new ExpiryModel(lifetime, sliding);
    }

    /// <summary><c>TimeSpan.FromX(sabit...)</c> biçimindeki bir ifadenin süresini hesaplar; başka biçimde <c>null</c> döner.</summary>
    private static long? TimeSpanTicks(SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken cancellationToken)
    {
        if (expression is not InvocationExpressionSyntax invocation
            || semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol { IsStatic: true } method
            || method.ContainingType.ToDisplayString() != "System.TimeSpan"
            || UnitTicks(method.Name.StartsWith("From", StringComparison.Ordinal) ? method.Name.Substring(4) : string.Empty) is not { } methodUnit)
        {
            return null;
        }

        var total = 0d;

        for (var index = 0; index < invocation.ArgumentList.Arguments.Count; index++)
        {
            var argument = invocation.ArgumentList.Arguments[index];
            var parameter = argument.NameColon is { } named
                ? method.Parameters.FirstOrDefault(candidate => candidate.Name == named.Name.Identifier.ValueText)
                : index < method.Parameters.Length ? method.Parameters[index] : null;

            // Tek parametreli biçimde (FromMinutes(double value)) birim metot adından, çok parametreli biçimde
            // (FromHours(int hours, long minutes = 0, ...)) parametre adından gelir.
            var unit = parameter is null ? null : UnitTicks(parameter.Name) ?? (parameter.Name == "value" ? methodUnit : null);

            if (unit is null
                || semanticModel.GetConstantValue(argument.Expression, cancellationToken) is not { HasValue: true, Value: { } value })
            {
                return null;
            }

            total += Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) * unit.Value;
        }

        return invocation.ArgumentList.Arguments.Count == 0 ? null : (long)total;
    }

    private static long? UnitTicks(string unit) => unit.ToLowerInvariant() switch
    {
        "days" => TimeSpan.TicksPerDay,
        "hours" => TimeSpan.TicksPerHour,
        "minutes" => TimeSpan.TicksPerMinute,
        "seconds" => TimeSpan.TicksPerSecond,
        "milliseconds" => TimeSpan.TicksPerMillisecond,
        "microseconds" => TimeSpan.TicksPerMillisecond / 1000,
        "ticks" => 1,
        _ => null,
    };
}
