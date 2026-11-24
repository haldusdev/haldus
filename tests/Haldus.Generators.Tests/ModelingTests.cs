namespace Haldus.Generators.Tests;

/// <summary>Tip–örnek (<c>TypedBy</c>), rol (<c>RoleOf</c>) ve genelleme (<c>SubtypeOf</c>).</summary>
public sealed class ModelingTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum VehicleStatus { Active, Retired }

            public partial class DeviceModel
            {
                public string Name { get; set; } = "";
                public int WarrantyMonths { get; set; }
                public string? Manual { get; set; }
                public decimal Weight { get; set; }
            }

            public partial class Device : IComputed
            {
                public string SerialNumber { get; set; } = "";
                public int? WarrantyMonths { get; set; }
                public string? Manual { get; set; }

                // Tipi farklı: devralınmaz.
                public int? Weight { get; set; }

                // Boş bırakılamaz: devralınacak bir şey yok.
                public string Name { get; set; } = "";

                public bool Covered { get; private set; }

                public void Compute() => Covered = WarrantyMonths > 0;
            }

            public partial class Person
            {
                public string FullName { get; set; } = "";
            }

            public partial class Employee
            {
                public string? Department { get; set; }
            }

            public partial class Vehicle
            {
                public string Plate { get; set; } = "";
            }

            public partial class Car : Vehicle
            {
                public int Seats { get; set; }
            }

            public partial class Truck : Vehicle
            {
                public decimal Payload { get; set; }
            }

            public partial class Boat
            {
                public int Length { get; set; }
            }

            public partial class Wheel
            {
                public int Size { get; set; }
            }

            public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options);
        }
        """;

    private static GeneratorRun Run(string body) =>
        GeneratorHarness.Run($$"""
            {{Entities}}

            namespace Shop
            {
                public static class Startup
                {
                    public static void Configure(IServiceCollection services) => services.AddHaldus<AppDbContext>();

                    public static void Map(IEndpointRouteBuilder app)
                    {
                        {{body}}
                    }
                }
            }
            """);

    // ── Tip–örnek ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TypedBy_InheritsBlankFieldsBeforeTheComputedOnes()
    {
        var run = Run("""
            app.MapCrud<DeviceModel>();
            app.MapCrud<Device>().TypedBy<DeviceModel>().Batch().Cloneable();
            """).AssertCompiles();

        // Tip zorunlu bir referanstır ve yalnızca oluştururken yazılır.
        var dtos = run.Sources["Shop.Device.Dtos.g.cs"];
        Assert.Contains("public global::System.Guid DeviceModelId { get; init; }", Between(dtos, "record CreateDeviceRequest", "record UpdateDeviceRequest"));
        Assert.DoesNotContain("DeviceModelId", Between(dtos, "record UpdateDeviceRequest", "record PatchDeviceRequest"));

        var endpoints = run.Sources["Shop.Device.Endpoints.g.cs"];

        // Yalnızca boş bırakılabilen, tipte aynı adla ve aynı türde bulunan alanlar devralınır.
        Assert.Contains("entity.WarrantyMonths ??= typeDefaults.WarrantyMonths;", endpoints);
        Assert.Contains("entity.Manual ??= typeDefaults.Manual;", endpoints);
        Assert.DoesNotContain("typeDefaults.Weight", endpoints);
        Assert.DoesNotContain("typeDefaults.Name", endpoints);

        // Devralma hesaplanan alanlardan önce, tip kaynağın kendi filtrelerinden okunarak yapılır; her yazma yolunda.
        foreach (var name in new[] { "CreateAsync", "UpdateAsync", "PatchAsync", "BatchCreateItemAsync", "BatchUpdateItemAsync", "CloneAsync" })
        {
            var handler = Handler(endpoints, name);
            var inherited = handler.IndexOf("var typeDefaults = await contexts.For<global::Shop.DeviceModel>().Set<global::Shop.DeviceModel>()", StringComparison.Ordinal);
            var computed = handler.IndexOf("((global::Haldus.IComputed)entity).Compute();", StringComparison.Ordinal);
            Assert.True(inherited >= 0 && inherited < computed, name);
            Assert.DoesNotContain("IgnoreQueryFilters", handler);
        }

        // Kayıtları olan tip silinemez.
        Assert.Contains("HasForeignKey(item => item.DeviceModelId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)", run.Sources["HaldusModule.g.cs"]);
    }

    // ── Rol ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void RoleOf_MakesThePlayerTheNaturalKey()
    {
        var run = Run("""
            app.MapCrud<Person>();
            app.MapCrud<Employee>().RoleOf<Person>();
            """).AssertCompiles();

        var dtos = run.Sources["Shop.Employee.Dtos.g.cs"];
        Assert.Contains("PersonId", Between(dtos, "record CreateEmployeeRequest", "record UpdateEmployeeRequest"));
        Assert.DoesNotContain("PersonId", Between(dtos, "record UpdateEmployeeRequest", "record PatchEmployeeRequest"));

        // Bir oyuncunun en çok bir rol kaydı olur ve rol, oyuncunun kimliğiyle de okunur.
        var endpoints = run.Sources["Shop.Employee.Endpoints.g.cs"];
        Assert.Contains("""MapGet("/by-person-id/{personId:guid}", GetByPersonIdAsync)""", endpoints);
        Assert.Contains("Duplicate(messages, \"Employee\", \"personId\"", Handler(endpoints, "CreateAsync"));

        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("entity.HasIndex(item => item.PersonId).IsUnique().HasDatabaseName(\"UX_Employees_PersonId\");", module);

        // Oyuncu silinince rolü de silinir.
        Assert.Contains("HasForeignKey(item => item.PersonId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", module);
    }

    [Fact]
    public void RoleOf_OfATenantRecord_DoesNotCascadeFromAGlobalPlayer()
    {
        // Genel bir kişi silinince kiracıların rol kayıtları kendiliğinden silinmez: zincirleme silme kiracı sınırını aşmaz.
        var mixed = Run("""
            app.MapCrud<Person>();
            app.MapCrud<Employee>().MultiTenant().RoleOf<Person>();
            """).AssertCompiles().Sources["HaldusModule.g.cs"];

        Assert.Contains("HasForeignKey(item => item.PersonId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict)", mixed);

        // Rol kiracı başına benzersizdir: aynı kişi her kiracıda ayrı bir rol kaydı taşıyabilir.
        Assert.Contains("entity.HasIndex(item => new { item.TenantId, item.PersonId }).IsUnique()", mixed);

        // İkisi de aynı kiracıdaysa veya geliştirici açıkça seçtiyse zincirleme silinir.
        var sameTenant = Run("""
            app.MapCrud<Person>().MultiTenant();
            app.MapCrud<Employee>().MultiTenant().RoleOf<Person>();
            """).AssertCompiles().Sources["HaldusModule.g.cs"];
        var chosen = Run("""
            app.MapCrud<Person>();
            app.MapCrud<Employee>().MultiTenant().RoleOf<Person>(OnDelete.Cascade);
            """).AssertCompiles().Sources["HaldusModule.g.cs"];

        Assert.Contains("HasForeignKey(item => item.PersonId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", sameTenant);
        Assert.Contains("HasForeignKey(item => item.PersonId).OnDelete(global::Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade)", chosen);
    }

    [Fact]
    public void TypedBy_DoesNotInheritSensitiveFields()
    {
        // Tipin hassas alanı, kaydın okunabilir aynı adlı alanına taşınmaz; kaydın hassas alanı da devralmaz.
        var typeSecret = Run("""
            app.MapCrud<DeviceModel>().Sensitive(model => model.Manual);
            app.MapCrud<Device>().TypedBy<DeviceModel>();
            """).AssertCompiles().Sources["Shop.Device.Endpoints.g.cs"];
        var recordSecret = Run("""
            app.MapCrud<DeviceModel>();
            app.MapCrud<Device>().TypedBy<DeviceModel>().Sensitive(device => device.Manual);
            """).AssertCompiles().Sources["Shop.Device.Endpoints.g.cs"];

        // Şifreli saklanan alan da yalnızca yazılır; o da iki yönde devralınmaz.
        var typeCipher = Run("""
            app.MapCrud<DeviceModel>().Encrypted(model => model.Manual);
            app.MapCrud<Device>().TypedBy<DeviceModel>();
            """).AssertCompiles().Sources["Shop.Device.Endpoints.g.cs"];
        var recordCipher = Run("""
            app.MapCrud<DeviceModel>();
            app.MapCrud<Device>().TypedBy<DeviceModel>().Encrypted(device => device.Manual);
            """).AssertCompiles().Sources["Shop.Device.Endpoints.g.cs"];

        foreach (var endpoints in new[] { typeSecret, recordSecret, typeCipher, recordCipher })
        {
            Assert.Contains("entity.WarrantyMonths ??= typeDefaults.WarrantyMonths;", endpoints);
            Assert.DoesNotContain("typeDefaults.Manual", endpoints);
        }
    }

    // ── Genelleme ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SubtypeOf_SharesTheTableOfTheBase()
    {
        var run = Run("""
            app.MapCrud<Vehicle>().MultiTenant().Auditable().SoftDeletable().ConcurrencyChecked();
            app.MapCrud<Car>().SubtypeOf<Vehicle>();
            app.MapCrud<Truck>().SubtypeOf<Vehicle>().Sortable(truck => truck.Payload);
            """).AssertCompiles();

        // Alt tip için hiçbir alan üretilmez: anahtarı ve trait alanlarını üst tipinden miras alır.
        Assert.DoesNotContain("Shop.Car.Entity.g.cs", run.Sources.Keys);
        Assert.Contains("public string Kind { get; set; } = \"\";", run.Sources["Shop.Vehicle.Entity.g.cs"]);

        // Tablo, anahtar, filtreler ve ayırt edici alan yalnızca üst tipte kurulur.
        var module = run.Sources["HaldusModule.g.cs"];
        Assert.Contains("modelBuilder.Entity<global::Shop.Car>().HasBaseType<global::Shop.Vehicle>();", module);
        Assert.Contains(
            "entity.HasDiscriminator(item => item.Kind).HasValue<global::Shop.Vehicle>(\"vehicle\").HasValue<global::Shop.Car>(\"car\").HasValue<global::Shop.Truck>(\"truck\");",
            module);
        Assert.Contains("entity.Property(item => item.Kind).HasMaxLength(64).HasSentinel(string.Empty);", module);
        Assert.DoesNotContain("modelBuilder.Entity<global::Shop.Car>(entity =>", module);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(module, "HaldusFilters.Tenant,"));

        // Alt tipin yanıtı üst tipin alanlarını, trait alanlarını ve kendi alanını taşır.
        var response = Between(run.Sources["Shop.Car.Dtos.g.cs"], "record CarResponse", "record CreateCarRequest");
        foreach (var field in new[] { " Id ", " Plate ", " Seats ", " CreatedAt ", " DeletedAt ", " Kind " })
        {
            Assert.Contains(field, response);
        }

        // Alt tipin uçları üst tipin trait'leriyle çalışır: kiracı zorunluluğu, sürüm, yumuşak silmenin uçları.
        var endpoints = run.Sources["Shop.Car.Endpoints.g.cs"];
        Assert.Contains("endpoints.MapGroup(\"/cars\")", endpoints);
        Assert.Contains("HaldusTenantRequiredFilter", endpoints);
        Assert.Contains("""MapPost("/{id:guid}/restore", RestoreAsync)""", endpoints);
        Assert.Contains("global::Haldus.HaldusETag.ReadIfMatch(httpContext)", Handler(endpoints, "UpdateAsync"));
        Assert.Contains("contexts.For<Car>().Set<Car>()", Handler(endpoints, "ListAsync"));

        // Tür istekle yazılamaz; üst tipin listesi türe göre daraltılır.
        Assert.DoesNotContain("Kind", run.Sources["Shop.Car.Dtos.g.cs"].Substring(run.Sources["Shop.Car.Dtos.g.cs"].IndexOf("record CreateCarRequest", StringComparison.Ordinal)));
        var list = Handler(run.Sources["Shop.Vehicle.Endpoints.g.cs"], "ListAsync");
        Assert.Contains("string? kind,", list);
        Assert.Contains("query = query.Where(item => item.Kind == kind);", list);
        Assert.DoesNotContain("string? kind,", Handler(endpoints, "ListAsync"));
    }

    [Theory]
    [InlineData("app.MapCrud<Device>().TypedBy<DeviceModel>();", "HALDUS007", "TypedBy<DeviceModel>() needs 'DeviceModel' to be mapped")]
    [InlineData("app.MapCrud<DeviceModel>(); app.MapCrud<Person>(); app.MapCrud<Device>().TypedBy<DeviceModel>().TypedBy<Person>();", "HALDUS007", "can have only one type")]
    [InlineData("app.MapCrud<DeviceModel>(); app.MapSingleton<Device>().TypedBy<DeviceModel>();", "HALDUS007", "TypedBy")]
    [InlineData("app.MapCrud<Person>(); app.MapCrud<Employee>().RoleOf<Person>().NaturalKey(employee => employee.Department);", "HALDUS007", "RoleOf makes the player the natural key")]
    [InlineData("app.MapCrud<Person>(); app.MapCrud<DeviceModel>(); app.MapCrud<Employee>().RoleOf<Person>().RoleOf<DeviceModel>();", "HALDUS007", "RoleOf makes the player the natural key")]
    [InlineData("app.MapCrud<Employee>().RoleOf<Person>();", "HALDUS007", "RoleOf<Person>() needs 'Person' to be mapped")]
    [InlineData("app.MapCrud<Car>().SubtypeOf<Vehicle>();", "HALDUS009", "Vehicle")]
    [InlineData("app.MapCrud<Vehicle>(); app.MapCrud<Boat>().SubtypeOf<Vehicle>();", "HALDUS007", "'Boat' must derive from 'Vehicle'")]
    [InlineData("app.MapCrud<Vehicle>().Workflow<VehicleStatus>(); app.MapCrud<Car>().SubtypeOf<Vehicle>();", "HALDUS007", "cannot have subtypes while it declares Workflow")]
    [InlineData("app.MapCrud<Vehicle>().Revisioned(); app.MapCrud<Car>().SubtypeOf<Vehicle>();", "HALDUS007", "cannot have subtypes while it declares Revisioned")]
    [InlineData("app.MapReadOnly<Vehicle>(); app.MapCrud<Car>().SubtypeOf<Vehicle>();", "HALDUS007", "it must be mapped with MapCrud")]
    [InlineData("app.MapCrud<Vehicle>(); app.MapCrud<Car>().SubtypeOf<Vehicle>().SoftDeletable();", "HALDUS007", "SoftDeletable cannot be declared on a subtype")]
    [InlineData("app.MapCrud<Vehicle>(); app.MapReadOnly<Car>().SubtypeOf<Vehicle>();", "HALDUS007", "SubtypeOf cannot be combined with MapReadOnly")]
    [InlineData("app.MapCrud<Vehicle>(); var cars = app.MapCrud<Car>().SubtypeOf<Vehicle>(); cars.MapDetail<Wheel>();", "HALDUS007", "takes part in a type hierarchy and cannot own embedded details")]
    public void Declarations_ThatCannotWork_AreRejected(string body, string expectedId, string expectedMessage)
    {
        var run = Run(body);
        Assert.Contains(run.GeneratorDiagnostics, candidate => candidate.Id == expectedId && candidate.GetMessage().Contains(expectedMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void ModelingSnapshot()
    {
        var run = Run("""
            app.MapCrud<DeviceModel>();
            app.MapCrud<Device>().TypedBy<DeviceModel>();
            app.MapCrud<Person>();
            app.MapCrud<Employee>().RoleOf<Person>();
            app.MapCrud<Vehicle>().Auditable();
            app.MapCrud<Car>().SubtypeOf<Vehicle>();
            """).AssertCompiles();

        Snapshot.Verify(run);
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"'{from}' … '{to}' not found");
        return text.Substring(start, end - start);
    }

    /// <summary>Bir handler metodunun gövdesi. Adın çağrıldığı yerler değil tanımlandığı yer aranır.</summary>
    private static string Handler(string endpoints, string name)
    {
        var from = endpoints.IndexOf($" {name}(\n", StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{name}' is not declared");

        var to = endpoints.IndexOf("\n    }\n", from, StringComparison.Ordinal);
        return endpoints.Substring(from, to - from);
    }
}
