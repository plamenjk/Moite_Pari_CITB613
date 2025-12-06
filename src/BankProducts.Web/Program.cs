// Program.cs
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BankProducts.Core;
using BankProducts.Data;
using BankProducts.Web.Infrastructure;
using static BankProducts.Web.Infrastructure.DbHelpers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages().AddRazorRuntimeCompilation();

// DB provider
var dbProv = builder.Configuration["Database:Provider"] ?? "SQLite";
IDbConnectionFactory dbf = dbProv.Equals("MySQL", StringComparison.OrdinalIgnoreCase)
    ? new MySqlConnFactory(builder.Configuration["Database:ConnectionStrings:MySQL"]!)
    : new SqliteConnFactory(builder.Configuration["Database:ConnectionStrings:SQLite"]!);
builder.Services.AddSingleton(dbf);

// DI
builder.Services.AddSingleton<DepositCalcService>();
builder.Services.AddSingleton<LoanCalcService>();
builder.Services.AddSingleton<DeposRepo>();
builder.Services.AddSingleton<LoansRepo>();
builder.Services.AddSingleton(new ScraperService(
    dbf,
    builder.Configuration.GetSection("Scraper:Banks").Get<string[]>() ?? Array.Empty<string>()));
builder.Services.AddHostedService<BgUpdate>();
builder.Services.AddSingleton<FxRatesService>();
builder.Services.AddHostedService<FxRatesBackground>();

// Ensure DB (seed) + миграции за новите таблици/колони
if (dbf is SqliteConnFactory s) DbInit.EnsureSqlite(s.ConnString);
EnsureSchemaUpgrades(dbf);
// почисти „примерни“ pending-и (без SourceUrl)
CleanSeedPending(dbf);

var app = builder.Build();

app.UseDeveloperExceptionPage();
app.UseStatusCodePages();

// глобално error handling (за да не връща HTML при JSON)
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex)
    {
        Console.WriteLine("FATAL: " + ex);
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync("Internal error: " + ex.Message);
    }
});

app.UseStaticFiles();
app.MapRazorPages();

// ---------------- Admin endpoints (без логин) ----------------
app.MapPost("/admin/run-scrape", async (ScraperService s) =>
{
    var created = await s.CheckForUpdatesAsync();
    return Results.Json(new { created });
});

// Одобрения/откази за депозити
app.MapPost("/admin/updates/{id:int}/approve", async (int id, IDbConnectionFactory dbf) =>
{
    using var con = dbf.Create(); ((dynamic)con).Open();
    var nowSql = NowSqlFor(con);

    using var sel = ((dynamic)con).CreateCommand();
    sel.CommandText = "SELECT DepositProductId, Field, OldValue, NewValue FROM PendingDepositUpdate WHERE Id=@id AND Status=0";
    var p = sel.CreateParameter(); p.ParameterName = "@id"; p.Value = id; sel.Parameters.Add(p);

    using var r = ((dynamic)sel).ExecuteReader();
    if (!r.Read()) return Results.NotFound();

    var depId = r.GetInt32(0);
    var field = r.GetString(1);
    var oldv = r.GetString(2);
    var newv = r.GetString(3);

    using var up = ((dynamic)con).CreateCommand();
    if (field == "RateAnnualPercent") up.CommandText = "UPDATE DepositProduct SET RateAnnualPercent=@nv WHERE Id=@did";
    else if (field == "MinAmount")    up.CommandText = "UPDATE DepositProduct SET MinAmount=@nv WHERE Id=@did";
    else if (field == "TermMonths")   up.CommandText = "UPDATE DepositProduct SET TermMonths=@nv WHERE Id=@did";
    else if (field == "DayCountBasis")up.CommandText = "UPDATE DepositProduct SET DayCountBasis=@nv WHERE Id=@did";
    else return Results.BadRequest("Unsupported field");

    var p1 = up.CreateParameter(); p1.ParameterName = "@nv";  p1.Value = newv;   up.Parameters.Add(p1);
    var p2 = up.CreateParameter(); p2.ParameterName = "@did"; p2.Value = depId; up.Parameters.Add(p2);
    ((dynamic)up).ExecuteNonQuery();

    if (field == "RateAnnualPercent")
    {
        using var hist = ((dynamic)con).CreateCommand();
        hist.CommandText = $"INSERT INTO DepositRateHistory(DepositProductId,ChangedAt,OldRate,NewRate,Source) VALUES(@d,{nowSql},@o,@n,'admin-approve')";
        var h1 = hist.CreateParameter(); h1.ParameterName = "@d"; h1.Value = depId; hist.Parameters.Add(h1);
        var h2 = hist.CreateParameter(); h2.ParameterName = "@o"; h2.Value = decimal.Parse(oldv, System.Globalization.CultureInfo.InvariantCulture); hist.Parameters.Add(h2);
        var h3 = hist.CreateParameter(); h3.ParameterName = "@n"; h3.Value = decimal.Parse(newv, System.Globalization.CultureInfo.InvariantCulture); hist.Parameters.Add(h3);
        ((dynamic)hist).ExecuteNonQuery();
    }

    using var upd = ((dynamic)con).CreateCommand();
    upd.CommandText = $"UPDATE PendingDepositUpdate SET Status=1, DecidedAt={nowSql} WHERE Id=@id";
    var u = upd.CreateParameter(); u.ParameterName = "@id"; u.Value = id; upd.Parameters.Add(u);
    ((dynamic)upd).ExecuteNonQuery();

    return Results.Ok(new { approved = id });
});

app.MapPost("/admin/updates/{id:int}/reject", async (int id, IDbConnectionFactory dbf) =>
{
    using var con = dbf.Create(); ((dynamic)con).Open();
    var nowSql = NowSqlFor(con);

    using var upd = ((dynamic)con).CreateCommand();
    upd.CommandText = $"UPDATE PendingDepositUpdate SET Status=2, DecidedAt={nowSql} WHERE Id=@id AND Status=0";
    var p = upd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; upd.Parameters.Add(p);
    var n = (int)((dynamic)upd).ExecuteNonQuery();
    if (n == 0) return Results.NotFound();
    return Results.Ok(new { rejected = id });
});

// Одобрения/откази за кредити (APR)
app.MapPost("/admin/loan-updates/{id:int}/approve", async (int id, IDbConnectionFactory dbf) =>
{
    using var con = dbf.Create(); ((dynamic)con).Open();
    var nowSql = NowSqlFor(con);

    using var sel = ((dynamic)con).CreateCommand();
    sel.CommandText = "SELECT LoanProductId, Field, OldValue, NewValue FROM PendingLoanUpdate WHERE Id=@id AND Status=0";
    var p = sel.CreateParameter(); p.ParameterName = "@id"; p.Value = id; sel.Parameters.Add(p);

    using var r = ((dynamic)sel).ExecuteReader();
    if (!r.Read()) return Results.NotFound();

    var loanId = r.GetInt32(0);
    var field  = r.GetString(1);
    var oldv   = r.GetString(2);
    var newv   = r.GetString(3);

    using var up = ((dynamic)con).CreateCommand();
    if (field == "RepresentativeAPRPercent") up.CommandText = "UPDATE LoanProduct SET RepresentativeAPRPercent=@nv WHERE Id=@lid";
    else return Results.BadRequest("Unsupported field for loan");

    var p1 = up.CreateParameter(); p1.ParameterName = "@nv";  p1.Value = newv;   up.Parameters.Add(p1);
    var p2 = up.CreateParameter(); p2.ParameterName = "@lid"; p2.Value = loanId; up.Parameters.Add(p2);
    ((dynamic)up).ExecuteNonQuery();

    using var hist = ((dynamic)con).CreateCommand();
    hist.CommandText = $"INSERT INTO LoanAprHistory(LoanProductId,ChangedAt,OldApr,NewApr,Source) VALUES(@l,{nowSql},@o,@n,'admin-approve')";
    var h1 = hist.CreateParameter(); h1.ParameterName = "@l"; h1.Value = loanId; hist.Parameters.Add(h1);
    var h2 = hist.CreateParameter(); h2.ParameterName = "@o";
    decimal.TryParse(oldv, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal o);
    h2.Value = o;
    hist.Parameters.Add(h2);
    var h3 = hist.CreateParameter(); h3.ParameterName = "@n"; h3.Value = decimal.Parse(newv, System.Globalization.CultureInfo.InvariantCulture); hist.Parameters.Add(h3);
    ((dynamic)hist).ExecuteNonQuery();

    using var upd = ((dynamic)con).CreateCommand();
    upd.CommandText = $"UPDATE PendingLoanUpdate SET Status=1, DecidedAt={nowSql} WHERE Id=@id";
    var u = upd.CreateParameter(); u.ParameterName = "@id"; u.Value = id; upd.Parameters.Add(u);
    ((dynamic)upd).ExecuteNonQuery();

    return Results.Ok(new { approved = id });
});

app.MapPost("/admin/loan-updates/{id:int}/reject", async (int id, IDbConnectionFactory dbf) =>
{
    using var con = dbf.Create(); ((dynamic)con).Open();
    var nowSql = NowSqlFor(con);

    using var upd = ((dynamic)con).CreateCommand();
    upd.CommandText = $"UPDATE PendingLoanUpdate SET Status=2, DecidedAt={nowSql} WHERE Id=@id AND Status=0";
    var p = upd.CreateParameter(); p.ParameterName = "@id"; p.Value = id; upd.Parameters.Add(p);
    var n = (int)((dynamic)upd).ExecuteNonQuery();
    if (n == 0) return Results.NotFound();
    return Results.Ok(new { rejected = id });
});

// ---------------- API ----------------
app.MapGet("/api/fx/latest", async (FxRatesService fx) => Results.Json(await fx.GetLatest()));

app.MapGet("/api/deposits/search.json", async (BankProducts.Data.DeposRepo repo, HttpRequest req) =>
{
    string? bank = req.Query["bank"]; string? cur = req.Query["cur"];
    int? minTerm = int.TryParse(req.Query["minTerm"], out var a) ? a : null;
    int? maxTerm = int.TryParse(req.Query["maxTerm"], out var b) ? b : null;
    decimal? minAmt = decimal.TryParse(req.Query["minAmt"], out var c) ? c : null;
    decimal? maxRate = decimal.TryParse(req.Query["maxRate"], out var d) ? d : null;
    var list = await repo.Search(bank, cur, minTerm, maxTerm, minAmt, maxRate);
    return Results.Json(list, new JsonSerializerOptions { WriteIndented = true });
});

app.MapGet("/api/deposits/search.csv", async (BankProducts.Data.DeposRepo repo, HttpRequest req) =>
{
    string? bank = req.Query["bank"]; string? cur = req.Query["cur"];
    int? minTerm = int.TryParse(req.Query["minTerm"], out var a) ? a : null;
    int? maxTerm = int.TryParse(req.Query["maxTerm"], out var b) ? b : null;
    decimal? minAmt = decimal.TryParse(req.Query["minAmt"], out var c) ? c : null;
    decimal? maxRate = decimal.TryParse(req.Query["maxRate"], out var d) ? d : null;
    var list = await repo.Search(bank, cur, minTerm, maxTerm, minAmt, maxRate);
    var sb = new StringBuilder().AppendLine("Bank,Name,Currency,TermMonths,MinAmount,RateAnnualPercent");
    foreach (var dpo in list) sb.AppendLine($"{dpo.BankName},{dpo.Name},{dpo.Currency},{dpo.TermMonths},{dpo.MinAmount},{dpo.RateAnnualPercent}");
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "deposits-search.csv");
});

app.MapGet("/api/loans/compare.json", async (BankProducts.Data.LoansRepo repo, LoanCalcService svc, string idsCsv, decimal principal, int term, decimal rate, decimal feeUp = 0, decimal feeM = 0) =>
{
    var ids = idsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var x) ? x : 0)
                    .Where(x => x > 0)
                    .ToArray();

    var prods = await repo.ByIds(ids);

    var rows = prods.Select(p =>
        svc.Calc(p, new LoanInput
        {
            ProductId = p.Id,
            Principal = principal,
            TermMonths = term,
            AnnualRatePercent = p.RepresentativeAPRPercent ?? rate,
            FeesUpfront = feeUp,
            MonthlyFee = feeM
        }));

    return Results.Json(rows, new JsonSerializerOptions { WriteIndented = true });
});

app.MapGet("/api/loans/compare.csv", async (BankProducts.Data.LoansRepo repo, LoanCalcService svc, string idsCsv, decimal principal, int term, decimal rate, decimal feeUp = 0, decimal feeM = 0) =>
{
    var ids = idsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var x) ? x : 0)
                    .Where(x => x > 0)
                    .ToArray();

    var prods = await repo.ByIds(ids);

    var sb = new StringBuilder().AppendLine("Bank,Name,Type,MonthlyPayment,APRPercent,TotalPaid,TotalInterest");
    foreach (var p in prods)
    {
        var r = svc.Calc(p, new LoanInput
        {
            ProductId = p.Id,
            Principal = principal,
            TermMonths = term,
            AnnualRatePercent = p.RepresentativeAPRPercent ?? rate,
            FeesUpfront = feeUp,
            MonthlyFee = feeM
        });

        sb.AppendLine($"{p.Bank},{p.Name},{p.Type},{r.MonthlyPayment},{r.APRPercent},{r.TotalPaid},{r.TotalInterest}");
    }
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "loans-compare.csv");
});

// health
app.MapGet("/health", () => Results.Ok(new { ok = true }));

app.Run();

// ---------------- Helpers / Migrations ----------------
namespace BankProducts.Web.Infrastructure
{
    public static class DbHelpers
    {
        public static string NowSqlFor(object conObj)
        {
            var typeName = conObj.GetType().FullName ?? "";
            return typeName.Contains("MySql", StringComparison.OrdinalIgnoreCase) ? "NOW()" : "datetime('now')";
        }

        public static void EnsureSchemaUpgrades(IDbConnectionFactory f)
        {
            using var con = f.Create(); ((dynamic)con).Open();
            var typeName = ((object)con).GetType().FullName ?? "";
            bool isMy = typeName.Contains("MySql", StringComparison.OrdinalIgnoreCase);

            // PendingLoanUpdate
            using (var cmd = ((dynamic)con).CreateCommand())
            {
                cmd.CommandText = isMy
                    ? @"CREATE TABLE IF NOT EXISTS PendingLoanUpdate (
                           Id INT AUTO_INCREMENT PRIMARY KEY,
                           LoanProductId INT NOT NULL,
                           Field VARCHAR(64) NOT NULL,
                           OldValue VARCHAR(128) NOT NULL,
                           NewValue VARCHAR(128) NOT NULL,
                           SourceUrl TEXT,
                           Status INT NOT NULL DEFAULT 0,
                           CreatedAt DATETIME NOT NULL,
                           DecidedAt DATETIME NULL
                       )"
                    : @"CREATE TABLE IF NOT EXISTS PendingLoanUpdate (
                           Id INTEGER PRIMARY KEY AUTOINCREMENT,
                           LoanProductId INTEGER NOT NULL,
                           Field TEXT NOT NULL,
                           OldValue TEXT NOT NULL,
                           NewValue TEXT NOT NULL,
                           SourceUrl TEXT,
                           Status INTEGER NOT NULL DEFAULT 0,
                           CreatedAt TEXT NOT NULL,
                           DecidedAt TEXT
                       )";
                ((dynamic)cmd).ExecuteNonQuery();
            }

            // LoanAprHistory
            using (var cmd = ((dynamic)con).CreateCommand())
            {
                cmd.CommandText = isMy
                    ? @"CREATE TABLE IF NOT EXISTS LoanAprHistory (
                           Id INT AUTO_INCREMENT PRIMARY KEY,
                           LoanProductId INT NOT NULL,
                           ChangedAt DATETIME NOT NULL,
                           OldApr DECIMAL(9,4) NOT NULL,
                           NewApr DECIMAL(9,4) NOT NULL,
                           Source VARCHAR(64) NOT NULL
                       )"
                    : @"CREATE TABLE IF NOT EXISTS LoanAprHistory (
                           Id INTEGER PRIMARY KEY AUTOINCREMENT,
                           LoanProductId INTEGER NOT NULL,
                           ChangedAt TEXT NOT NULL,
                           OldApr NUMERIC NOT NULL,
                           NewApr NUMERIC NOT NULL,
                           Source TEXT NOT NULL
                       )";
                ((dynamic)cmd).ExecuteNonQuery();
            }

            // Добавяме колона RepresentativeAPRPercent в LoanProduct, ако липсва
            if (isMy)
            {
                using var chk = ((dynamic)con).CreateCommand();
                chk.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='LoanProduct' AND COLUMN_NAME='RepresentativeAPRPercent'";
                var cnt = Convert.ToInt32(((dynamic)chk).ExecuteScalar() ?? 0);
                if (cnt == 0)
                {
                    using var add = ((dynamic)con).CreateCommand();
                    add.CommandText = "ALTER TABLE LoanProduct ADD COLUMN RepresentativeAPRPercent DECIMAL(9,4) NULL";
                    ((dynamic)add).ExecuteNonQuery();
                }
            }
            else
            {
                // SQLite
                var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (var info = ((dynamic)con).CreateCommand())
                {
                    info.CommandText = "PRAGMA table_info('LoanProduct')";
                    using var r = ((dynamic)info).ExecuteReader();
                    while (r.Read()) cols.Add(r.GetString(1));
                }
                if (!cols.Contains("RepresentativeAPRPercent"))
                {
                    using var add = ((dynamic)con).CreateCommand();
                    add.CommandText = "ALTER TABLE LoanProduct ADD COLUMN RepresentativeAPRPercent NUMERIC NULL";
                    ((dynamic)add).ExecuteNonQuery();
                }
            }
        }

        public static void CleanSeedPending(IDbConnectionFactory f)
        {
            try
            {
                using var con = f.Create(); ((dynamic)con).Open();
                foreach (var table in new[] { "PendingDepositUpdate", "PendingLoanUpdate" })
                {
                    using var cmd = ((dynamic)con).CreateCommand();
                    cmd.CommandText = $"DELETE FROM {table} WHERE SourceUrl IS NULL OR SourceUrl=''";
                    ((dynamic)cmd).ExecuteNonQuery();
                }
            }
            catch { /* ignore */ }
        }
    }
}

// ---------------- Services (Scrapers) ----------------
public class ScraperService
{
    private readonly IDbConnectionFactory _f;
    private readonly string[] _banks;

    public ScraperService(IDbConnectionFactory f, string[] banks)
    {
        _f = f;
        _banks = banks;
    }

    /// <summary>
    /// Главен метод – вика се от /admin/run-scrape и от BgUpdate.
    /// 1) Скрейпва депозити по банка (както досега).
    /// 2) Скрейпва ГПР за кредити по ПРОДУКТ (по SourceUrl на LoanProduct).
    /// </summary>
    public async Task<int> CheckForUpdatesAsync()
    {
        int created = 0;
        using var http = new HttpClient();

        // 1) Депозити – по банка (старото поведение)
        foreach (var bank in _banks)
        {
            try
            {
                var (depUrl, depRate) = await FetchDepositRateAsync(http, bank);
                if (depRate != null)
                {
                    created += UpsertDepositPending(bank, depUrl!, depRate.Value);
                }
            }
            catch
            {
                // игнор – единичните грешки не спират целия цикъл
            }
        }

        // 2) Кредити – по ПРОДУКТ (по SourceUrl)
        try
        {
            created += await CheckLoanProductsAprAsync(http);
        }
        catch
        {
            // по-добре нищо, отколкото да падне целият скрейп
        }

        return created;
    }

    // ---------------- deposits ----------------
    private int UpsertDepositPending(string bankKey, string url, decimal newRate)
    {
        int created = 0;
        using var con = _f.Create(); ((dynamic)con).Open();

        using var cmd = ((dynamic)con).CreateCommand();
        cmd.CommandText = "SELECT dp.Id, b.Name, dp.Name, dp.RateAnnualPercent FROM DepositProduct dp JOIN Bank b ON b.Id=dp.BankId WHERE b.Name LIKE @b";
        var p = cmd.CreateParameter(); p.ParameterName = "@b"; p.Value = "%" + bankKey + "%"; cmd.Parameters.Add(p);

        using var r = ((dynamic)cmd).ExecuteReader();
        var list = new List<(int id, string bank, string name, decimal rate)>();
        while (r.Read())
            list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3)));

        foreach (var it in list)
        {
            if (Math.Abs(it.rate - newRate) >= 0.01m)
            {
                using var ins = ((dynamic)con).CreateCommand();
                var nowSql = NowSqlFor(con);
                ins.CommandText =
                    $"INSERT INTO PendingDepositUpdate(DepositProductId,Field,OldValue,NewValue,SourceUrl,Status,CreatedAt) " +
                    $"VALUES(@d,'RateAnnualPercent',@o,@n,@s,0,{nowSql})";
                var p1 = ins.CreateParameter(); p1.ParameterName = "@d"; p1.Value = it.id; ins.Parameters.Add(p1);
                var p2 = ins.CreateParameter(); p2.ParameterName = "@o"; p2.Value = it.rate.ToString(System.Globalization.CultureInfo.InvariantCulture); ins.Parameters.Add(p2);
                var p3 = ins.CreateParameter(); p3.ParameterName = "@n"; p3.Value = newRate.ToString(System.Globalization.CultureInfo.InvariantCulture); ins.Parameters.Add(p3);
                var p4 = ins.CreateParameter(); p4.ParameterName = "@s"; p4.Value = url; ins.Parameters.Add(p4);
                created += ((dynamic)ins).ExecuteNonQuery();
            }
        }
        return created;
    }

    // Прости евристики: проценти и ГПР
    private static readonly Regex pct = new(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex aprRx = new(
        @"ГПР[^0-9]{0,20}(\d+(?:[.,]\d+)?)\s*%|APR[^0-9]{0,20}(\d+(?:[.,]\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private async Task<(string url, decimal?)> FetchDepositRateAsync(HttpClient http, string bank)
    {
        var url = bank switch
        {
            "DSK"       => "https://dskbank.bg",
            "Postbank"  => "https://www.postbank.bg",
            "UBB"       => "https://www.ubb.bg",
            "UniCredit" => "https://www.unicreditbulbank.bg",
            "ProCredit" => "https://www.procreditbank.bg",
            "Fibank"    => "https://www.fibank.bg",
            "BACB"      => "https://www.bacb.bg",
            "CCB"       => "https://www.ccbank.bg",
            "Allianz"   => "https://www.allianz.bg",
            "Investbank"=> "https://ibank.bg",
            "Municipal" => "https://www.municipalbank.bg",
            "TBI"       => "https://tbibank.bg",
            "Texim"     => "https://www.teximbank.bg",
            "IABank"    => "https://www.iabank.bg",
            "DBank"     => "https://www.dbank.bg",
            "Tokuda"    => "https://www.tokudabank.bg",
            "Bigbank"   => "https://www.bigbank.bg",
            _           => "https://example.com"
        };

        try
        {
            var html = await http.GetStringAsync(url);
            var m = pct.Match(html);
            if (m.Success)
            {
                var s = m.Groups[1].Value.Replace(',', '.');
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)
                    && val >= 0 && val <= 15)
                    return (url, val);
            }
        }
        catch { }

        return (url, null);
    }

    // ---------------- loans: per-product APR по SourceUrl ----------------

    /// <summary>
    /// Обхожда всички LoanProduct със SourceUrl и опитва да намери ГПР на самата продуктова страница.
    /// За всяка разлика над 0.01 в RepresentativeAPRPercent пуска PendingLoanUpdate.
    /// </summary>
    private async Task<int> CheckLoanProductsAprAsync(HttpClient http)
    {
        int created = 0;

        using var con = _f.Create(); ((dynamic)con).Open();

        using var cmd = ((dynamic)con).CreateCommand();
        cmd.CommandText = @"
SELECT Id, Bank, Name, RepresentativeAPRPercent, SourceUrl
FROM LoanProduct
WHERE SourceUrl IS NOT NULL AND SourceUrl <> ''";

        var products = new List<(int id, string bank, string name, decimal? apr, string url)>();

        using (var r = ((dynamic)cmd).ExecuteReader())
        {
            while (r.Read())
            {
                var id   = r.GetInt32(0);
                var bank = r.GetString(1);
                var name = r.GetString(2);
                decimal? apr = r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3);
                var url  = r.IsDBNull(4) ? "" : r.GetString(4);

                if (!string.IsNullOrWhiteSpace(url))
                    products.Add((id, bank, name, apr, url));
            }
        }

        foreach (var p in products)
        {
            try
            {
                var (url, aprNew) = await FetchLoanAprFromUrlAsync(http, p.url);
                if (aprNew == null) continue;

                var oldApr = p.apr ?? 0m;
                if (Math.Abs(oldApr - aprNew.Value) < 0.01m) continue; // няма съществена промяна

                using var ins = ((dynamic)con).CreateCommand();
                var nowSql = NowSqlFor(con);

                ins.CommandText =
                    $"INSERT INTO PendingLoanUpdate(LoanProductId,Field,OldValue,NewValue,SourceUrl,Status,CreatedAt) " +
                    $"VALUES(@l,'RepresentativeAPRPercent',@o,@n,@s,0,{nowSql})";

                var p1 = ins.CreateParameter(); p1.ParameterName = "@l"; p1.Value = p.id; ins.Parameters.Add(p1);
                var p2 = ins.CreateParameter(); p2.ParameterName = "@o"; p2.Value = oldApr.ToString(System.Globalization.CultureInfo.InvariantCulture); ins.Parameters.Add(p2);
                var p3 = ins.CreateParameter(); p3.ParameterName = "@n"; p3.Value = aprNew.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); ins.Parameters.Add(p3);
                var p4 = ins.CreateParameter(); p4.ParameterName = "@s"; p4.Value = url; ins.Parameters.Add(p4);

                created += ((dynamic)ins).ExecuteNonQuery();
            }
            catch
            {
                // ако конкретният продукт не стане – продължаваме с другите
            }
        }

        return created;
    }

    /// <summary>
    /// Взима HTML от конкретния URL на продукт и търси ГПР / APR.
    /// </summary>
    private async Task<(string url, decimal?)> FetchLoanAprFromUrlAsync(HttpClient http, string url)
    {
        try
        {
            var html = await http.GetStringAsync(url);

            // 1) първо търсим конкретно „ГПР ... %“ или „APR ... %“
            var mApr = aprRx.Match(html);
            if (mApr.Success)
            {
                var grp = mApr.Groups[1].Success ? mApr.Groups[1] : mApr.Groups[2];
                var s = grp.Value.Replace(',', '.');
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var apr)
                    && apr >= 0 && apr <= 40)
                {
                    return (url, apr);
                }
            }

            // 2) fallback – първият разумен процент
            var m = pct.Match(html);
            if (m.Success)
            {
                var s = m.Groups[1].Value.Replace(',', '.');
                if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var val)
                    && val >= 0 && val <= 40)
                {
                    return (url, val);
                }
            }
        }
        catch
        {
        }

        return (url, null);
    }
}

public class BgUpdate : BackgroundService
{
    private readonly ScraperService s; public BgUpdate(ScraperService s) { this.s = s; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await s.CheckForUpdatesAsync(); } catch { }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}

public class FxRatesService
{
    private Dictionary<string, object> _latest = new();
    private DateTime _updated = DateTime.MinValue;
    public async Task<Dictionary<string, object>> GetLatest()
    {
        if ((DateTime.UtcNow - _updated).TotalMinutes < 5 && _latest.Count > 0) return _latest;
        await Refresh(); return _latest;
    }
    public async Task Refresh()
    {
        try
        {
            using var http = new HttpClient();
            var url = "https://api.exchangerate.host/latest?base=BGN";
            var json = await http.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var dict = new Dictionary<string, object>();
            dict["base"] = doc.RootElement.GetProperty("base").GetString() ?? "BGN";
            dict["date"] = doc.RootElement.GetProperty("date").GetString() ?? "";
            var rates = new Dictionary<string, decimal>();
            foreach (var p in doc.RootElement.GetProperty("rates").EnumerateObject())
            {
                if (p.Name is "EUR" or "USD" or "GBP" or "CHF" or "JPY" or "CAD" or "AUD")
                {
                    rates[p.Name] = p.Value.GetDecimal();
                }
            }
            dict["rates"] = rates;
            _latest = dict; _updated = DateTime.UtcNow;
        }
        catch
        {
            _latest = new Dictionary<string, object> {
                ["base"]="BGN", ["date"]=DateTime.UtcNow.ToString("yyyy-MM-dd"),
                ["rates"]= new Dictionary<string,decimal>{{"EUR",0.51m},{"USD",0.56m},{"GBP",0.44m},{"CHF",0.50m}}
            };
            _updated = DateTime.UtcNow;
        }
    }
}

public class FxRatesBackground : BackgroundService
{
    private readonly FxRatesService fx; private readonly IConfiguration cfg;
    public FxRatesBackground(FxRatesService fx, IConfiguration cfg) { this.fx = fx; this.cfg = cfg; }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = cfg.GetValue<int?>("Fx:RefreshMinutes") ?? 60;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await fx.Refresh(); } catch { }
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }
}

namespace BankProducts.Web.Infrastructure
{
    public sealed class DbCfg
    {
        public string Conn { get; init; } = "";
    }
}
