using Microsoft.Data.Sqlite;
using MySqlConnector;
using System.Data;
using BankProducts.Core;

namespace BankProducts.Data;

public interface IDbConnectionFactory
{
    IDbConnection Create();
    string ConnString { get; }
}

public class SqliteConnFactory : IDbConnectionFactory
{
    public string ConnString { get; }
    public SqliteConnFactory(string cs) { ConnString = cs; }
    public IDbConnection Create() => new SqliteConnection(ConnString);
}

public class MySqlConnFactory : IDbConnectionFactory
{
    public string ConnString { get; }
    public MySqlConnFactory(string cs) { ConnString = cs; }
    public IDbConnection Create() => new MySqlConnection(ConnString);
}

public static class DbInit
{
    public static void EnsureSqlite(string connStr)
    {
        using var conn = new SqliteConnection(connStr);
        conn.Open();

        // --- схема ---
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Bank (
  Id   INTEGER PRIMARY KEY AUTOINCREMENT,
  Name TEXT NOT NULL UNIQUE,
  Web  TEXT
);

CREATE TABLE IF NOT EXISTS DepositProduct (
  Id                INTEGER PRIMARY KEY AUTOINCREMENT,
  BankId            INTEGER NOT NULL,
  Name              TEXT    NOT NULL,
  Currency          TEXT    NOT NULL,
  TermMonths        INTEGER NOT NULL,
  MinAmount         NUMERIC NOT NULL,
  AutoRenewal       INTEGER NOT NULL DEFAULT 1,
  DayCountBasis     TEXT,
  RateAnnualPercent NUMERIC NOT NULL,
  ValidFrom         TEXT,
  SourceUrl         TEXT
);

CREATE TABLE IF NOT EXISTS LoanProduct ( 
  Id                     INTEGER PRIMARY KEY AUTOINCREMENT,
  Bank                   TEXT NOT NULL,
  Name                   TEXT NOT NULL,
  Type                   INTEGER NOT NULL,
  SourceUrl              TEXT,
  MaxLTVPercent          NUMERIC NULL,
  CollateralRequired     INTEGER NULL,
  SalaryTransferRequired INTEGER NULL,
  Purpose                TEXT NULL,
  RepresentativeAPRPercent NUMERIC NULL
);

CREATE TABLE IF NOT EXISTS DepositRateHistory ( 
  Id               INTEGER PRIMARY KEY AUTOINCREMENT,
  DepositProductId INTEGER NOT NULL,
  ChangedAt        TEXT NOT NULL,
  OldRate          NUMERIC NOT NULL,
  NewRate          NUMERIC NOT NULL,
  Source           TEXT
);

CREATE TABLE IF NOT EXISTS PendingDepositUpdate ( 
  Id               INTEGER PRIMARY KEY AUTOINCREMENT,
  DepositProductId INTEGER NOT NULL,
  Field            TEXT NOT NULL,
  OldValue         TEXT NOT NULL,
  NewValue         TEXT NOT NULL,
  SourceUrl        TEXT,
  Status           INTEGER NOT NULL DEFAULT 0,
  CreatedAt        TEXT NOT NULL,
  DecidedAt        TEXT
);
";
        cmd.ExecuteNonQuery();

        // --- seed само ако базата е празна ---
        var chk = conn.CreateCommand();
        chk.CommandText = "SELECT COUNT(*) FROM Bank;";
        var cnt = (long)(chk.ExecuteScalar() ?? 0);
        if (cnt != 0) return;

        using var tx = conn.BeginTransaction();

        // ---------- Банки ----------
        var b = conn.CreateCommand();
        b.Transaction = tx;
        b.CommandText = @"
INSERT INTO Bank(Name,Web) VALUES
('ProCredit Bank','https://www.procreditbank.bg'),
('Postbank','https://www.postbank.bg'),
('ОББ (UBB)','https://www.ubb.bg'),
('UniCredit Bulbank','https://www.unicreditbulbank.bg'),
('BACB','https://www.bacb.bg'),
('CCB','https://www.ccbank.bg'),
('DSK Bank','https://dskbank.bg'),
('Allianz Bank','https://www.allianz.bg'),
('Fibank','https://www.fibank.bg'),
('TBI Bank','https://tbibank.bg'),
('Investbank','https://ibank.bg'),
('Municipal Bank','https://www.municipalbank.bg'),
('Texim Bank','https://www.teximbank.bg'),
('IABank','https://www.iabank.bg'),
('DBank','https://www.dbank.bg'),
('Tokuda Bank','https://www.tokudabank.bg'),
('Bigbank','https://www.bigbank.bg');
";
        b.ExecuteNonQuery();

        // IDs на банките (по реда на insert-а):
        // 1 ProCredit, 2 Postbank, 3 ОББ (UBB), 4 UniCredit,
        // 5 BACB, 6 CCB, 7 DSK, 8 Allianz,
        // 9 Fibank, 10 TBI, 11 Investbank, 12 Municipal,
        // 13 Texim, 14 IABank, 15 DBank, 16 Tokuda, 17 Bigbank

        // ---------- Реални депозитни продукти ----------
        var d = conn.CreateCommand();
        d.Transaction = tx;
        d.CommandText = @"
INSERT INTO DepositProduct
  (BankId, Name, Currency, TermMonths, MinAmount, AutoRenewal, DayCountBasis, RateAnnualPercent, ValidFrom, SourceUrl)
VALUES
  -- ProCredit Bank – Срочен депозит
  (1,'Срочен депозит 6М','BGN', 6, 1000,1,'360',1.20,'2025-01-01','https://www.procreditbank.bg/bg/individualni-klienti/spestiavania/srochen-depozit'),
  (1,'Срочен депозит 12М','BGN',12, 1000,1,'360',1.50,'2025-01-01','https://www.procreditbank.bg/bg/individualni-klienti/spestiavania/srochen-depozit'),

  -- Postbank – срочни депозити
  (2,'Депозит „За всеки“ 12М','BGN',12, 1000,1,'360',2.10,'2025-01-01','https://www.postbank.bg/bg-BG/Individuals/Deposits/Deposit-for-Everyone'),
  (2,'Срочен депозит „3x3“','BGN', 9, 1000,1,'360',2.00,'2025-01-01','https://www.postbank.bg/bg-BG/Individuals/Deposits/Term_Deposit_3_x_3'),
  (2,'Депозит „Напред“ 6М','EUR', 6, 2000,0,'360',1.80,'2025-01-01','https://www.postbank.bg/bg-BG/Individuals/Deposits/Deposit-Napred'),

  -- ОББ (UBB) – стандартен и инвестиционен депозит
  (3,'Стандартен срочен депозит 12М','BGN',12, 2000,1,'360',1.30,'2025-01-01','https://www.ubb.bg/individual-clients/spestyavaniya-i-investitsii/standartni-srochni-depoziti'),
  (3,'Депозит Инвест 12М','BGN',12, 3000,0,'360',1.40,'2025-01-01','https://www.ubb.bg/individual-clients/spestyavaniya-i-investitsii/deposit-invest'),

  -- UniCredit Bulbank – Срочен депозит „Класика“
  (4,'Срочен депозит „Класика“ 6М','BGN', 6,  500,1,'360',1.10,'2025-01-01','https://www.unicreditbulbank.bg/bg/individualni-klienti/spestyavaniya-investitsii/depozit-klasika/'),
  (4,'Срочен депозит „Класика“ 12М','BGN',12, 500,1,'360',1.40,'2025-01-01','https://www.unicreditbulbank.bg/bg/individualni-klienti/spestyavaniya-investitsii/depozit-klasika/'),

  -- BACB – стандартен, онлайн и „Всичко е точно“
  (5,'Стандартен срочен депозит 12М','BGN',12, 500,1,'360',1.75,'2025-01-01','https://www.bacb.bg/bg/individualni-klienti/depoziti-i-spestjavanija/depoziti/standarten-srochen-depozit'),
  (5,'Онлайн депозит 12М','BGN',12,1000,1,'360',1.90,'2025-01-01','https://www.bacb.bg/bg/individualni-klienti/depoziti-i-spestjavanija/depoziti/onlajn-depozit'),
  (5,'Депозит „Всичко е точно“ 24М','BGN',24,1000,1,'360',2.10,'2025-01-01','https://www.bacb.bg/bg/individualni-klienti/depoziti-i-spestjavanija/depoziti/depozit-vsichko-e-tochno'),

  -- CCB – стандартен депозит
  (6,'Стандартен депозит 6М','BGN', 6,  500,1,'360',0.80,'2025-01-01','https://www.ccbank.bg/bg/fizicheski-lica/depoziti-i-spestovni-smetki/srochni-depoziti/standarten-depozit'),
  (6,'Стандартен депозит 12М','BGN',12, 500,1,'360',1.00,'2025-01-01','https://www.ccbank.bg/bg/fizicheski-lica/depoziti-i-spestovni-smetki/srochni-depoziti/standarten-depozit'),

  -- DSK Bank – срочни депозити
  (7,'Срочен депозит ДСК 6М','BGN', 6, 1000,1,'360',0.90,'2025-01-01','https://dskbank.bg/individualni-klienti/spestyavane/srochni-depoziti'),
  (7,'Срочен депозит ДСК 12М','BGN',12, 1000,1,'360',1.10,'2025-01-01','https://dskbank.bg/individualni-klienti/spestyavane/srochni-depoziti'),

  -- Allianz Bank – стандартен срочен депозит и Комфорт
  (8,'Стандартен срочен депозит 12М','BGN',12, 500,1,'360',1.50,'2025-01-01','https://www.allianz.bg/bg_BG/individuals/banking/savings/standarten-srochen-deposit.html'),
  (8,'Депозит Комфорт 12М','BGN',12,1000,1,'360',1.70,'2025-01-01','https://www.allianz.bg/bg_BG/individuals/banking/savings/term-deposit-comfort.html'),

  -- Fibank – депозити
  (9,'Депозит „Традиция“ 12М','BGN',12, 500,1,'360',2.20,'2025-01-01','https://www.fibank.bg/web/files/richeditor/documents/tariff/uvedomleniya/Prilojenie_Uslovia_po_depozitni_produkti_02.08.2024.pdf'),
  (9,'Депозит „Експрес“ 24М','BGN',24,1000,1,'360',2.40,'2025-01-01','https://www.fibank.bg/web/files/richeditor/documents/tariff/uvedomleniya/Prilojenie_Uslovia_po_depozitni_produkti_02.08.2024.pdf'),

  -- TBI Bank – срочен депозит
  (10,'Срочен депозит 12М','BGN',12,1000,1,'360',2.00,'2025-01-01','https://tbibank.bg/lichno-bankirane/spestjavanija/depozit/'),
  (10,'Срочен депозит в мобилно приложение 36М','BGN',36,1000,1,'360',2.20,'2025-01-01','https://tbibank.bg/depozit-mobilno-prilojenie/'),

  -- Investbank – стандартен, онлайн и Старт
  (11,'Стандартен депозит 12М','BGN',12, 500,1,'360',1.60,'2025-01-01','https://ibank.bg/bg/fizicheski-lica/depoziti/standartni-depoziti-spisyk/standarten-depozit'),
  (11,'Онлайн депозит 12М','BGN',12,1000,1,'360',1.80,'2025-01-01','https://ibank.bg/bg/fizicheski-lica/depoziti/standartni-depoziti-spisyk/onlajn-depozit'),
  (11,'Депозит Старт 12М','BGN',12, 500,1,'360',1.70,'2025-01-01','https://ibank.bg/bg/fizicheski-lica/depoziti/standartni-depoziti-spisyk/depozit-start'),

  -- Municipal Bank – стандартен срочен влог
  (12,'Стандартен срочен депозит 6М','BGN', 6,  98,1,'360',0.90,'2025-01-01','https://www.municipalbank.bg/bg/standarten-srotchen-vlog'),

  -- Texim Bank – стандартен и онлайн депозит
  (13,'Стандартен срочен депозит 12М','BGN',12, 100,1,'360',1.20,'2025-01-01','https://www.teximbank.bg/fizicheski-litsa/spestiyavane/standarten-srochen-depozit/'),
  (13,'Депозит „Тексим Онлайн“ 12М','BGN',12,3000,1,'360',1.60,'2025-01-01','https://www.teximbank.bg/fizicheski-litsa/spestiyavane/depozit-texim-online/'),

  -- IABank – Асет срочен депозит
  (14,'Асет срочен депозит 12М','BGN',12,1000,1,'360',1.40,'2025-01-01','https://www.iabank.bg/fizicheski-lica/depoziti/depoziti-menu/aset-srochen-depozit'),

  -- DBank – Стандартен и „Макси“
  (15,'Стандартен депозит 12М','BGN',12, 500,1,'360',1.20,'2025-01-01','https://www.dbank.bg/bg/individualni-klienti/depoziti/standarten-depozit'),
  (15,'Д Банк МАКСИ 12М','BGN',12,1000,1,'360',1.25,'2025-01-01','https://www.dbank.bg/bg/individualni-klienti/depoziti/d-bank-maksi'),

  -- Tokuda Bank – Депозит Токуда Класик
  (16,'Депозит „Токуда Класик“ 12М','BGN',12, 100,1,'360',1.10,'2025-01-01','https://www.tokudabank.bg/bg/individual-clients/depoziti/depozit-tokuda-classic/'),

  -- Bigbank – срочен депозит
  (17,'Срочен депозит 12М','EUR',12, 1000,1,'360',2.00,'2025-01-01','https://www.bigbank.bg/deposit/');
";
        d.ExecuteNonQuery();

        // ---------- Кредити (потребителски + ипотечни) с RepresentativeAPRPercent ----------
        var loan = conn.CreateCommand();
        loan.Transaction = tx;
        loan.CommandText = @"
INSERT INTO LoanProduct 
  (Bank, Name, Type, SourceUrl, MaxLTVPercent, CollateralRequired, SalaryTransferRequired, Purpose, RepresentativeAPRPercent)
VALUES
  -- Потребителски кредити (Type = 1)
  ('Postbank','Потребителски кредит „Класик“',1,'https://www.postbank.bg/bg-BG/Individuals/Credits/Consumer-Loans/Classic-Consumer-Loan',NULL,NULL,1,'Свободно потребление',11.5),
  ('Postbank','Потребителски кредит „Нов старт“',1,'https://www.postbank.bg/bg-BG/Individuals/Credits/Consumer-Loans/New-Start',NULL,NULL,1,'Рефинансиране/потребление',12.2),
  ('DSK Bank','Потребителски кредит „Експрес“',1,'https://dskbank.bg/individualni-klienti/krediti/potrebitelski-kredit-ekspres',NULL,NULL,1,'Свободно потребление',10.8),
  ('DSK Bank','Потребителски кредит за обединяване на задължения',1,'https://dskbank.bg/individualni-klienti/krediti/obedini-svoite-krediti',NULL,NULL,1,'Обединяване на задължения',11.9),
  ('ОББ (UBB)','Потребителски кредит „Комфорт“',1,'https://www.ubb.bg/individual-clients/krediti/potrebitelski-kredit-komfort',NULL,NULL,1,'Свободно потребление',12.7),
  ('ОББ (UBB)','Потребителски кредит за рефинансиране',1,'https://www.ubb.bg/individual-clients/krediti/potrebitelski-kredit-refinansirane',NULL,NULL,1,'Рефинансиране',13.1),
  ('UniCredit Bulbank','Потребителски кредит „Експресо“',1,'https://www.unicreditbulbank.bg/bg/individualni-klienti/krediti/potrebitelski-krediti/ekspreso/',NULL,NULL,1,'Свободно потребление',10.9),
  ('UniCredit Bulbank','Потребителски кредит за обединяване на кредити',1,'https://www.unicreditbulbank.bg/bg/individualni-klienti/krediti/potrebitelski-krediti/obedini-kreditite-si/',NULL,NULL,1,'Обединяване на задължения',11.7),
  ('Fibank','Потребителски кредит „Комфорт“',1,'https://www.fibank.bg/bg/individualni-klienti/krediti/potrebitelski-krediti/kredit-komfort',NULL,NULL,1,'Свободно потребление',12.9),
  ('Fibank','Потребителски кредит „Съкровище“',1,'https://www.fibank.bg/bg/individualni-klienti/krediti/potrebitelski-krediti/kredit-sykrovishte',NULL,NULL,1,'Свободно/образование',13.4),
  ('TBI Bank','Потребителски кредит онлайн',1,'https://tbibank.bg/lichno-bankirane/krediti/potrebitelski-kredit-online/',NULL,NULL,1,'Онлайн потребление',14.8),
  ('TBI Bank','Кредит за обединяване',1,'https://tbibank.bg/lichno-bankirane/krediti/refinansirasht-kredit/',NULL,NULL,1,'Обединяване/рефинансиране',15.2),
  ('Investbank','Потребителски кредит „Стандарт“',1,'https://ibank.bg/bg/fizicheski-lica/krediti/potrebitelski-krediti/potrebitelski-kredit-standart',NULL,NULL,1,'Свободно потребление',12.0),
  ('IABank','Потребителски кредит „Асет“',1,'https://www.iabank.bg/fizicheski-lica/krediti/potrebitelski-krediti/aset-potrebitelski-kredit',NULL,NULL,1,'Свободно потребление',11.3),
  ('DBank','Потребителски кредит „Д Банк Личен“',1,'https://www.dbank.bg/bg/individualni-klienti/krediti/potrebitelski-kredit',NULL,NULL,1,'Свободно потребление',12.4),
  ('Texim Bank','Потребителски кредит „Тексим“',1,'https://www.teximbank.bg/fizicheski-litsa/krediti/potrebitelski-kredit/',NULL,NULL,1,'Свободно потребление',13.0),

  -- Ипотечни кредити (Type = 2)
  ('Postbank','Ипотечен кредит „Нов дом“',2,'https://www.postbank.bg/bg-BG/Individuals/Credits/Mortgage-Loans/New-Home-Mortgage',85,1,1,'Покупка на жилище',4.1),
  ('Postbank','Ипотечен кредит за рефинансиране',2,'https://www.postbank.bg/bg-BG/Individuals/Credits/Mortgage-Loans/Refinancing-Mortgage',85,1,1,'Рефинансиране на жилищен кредит',4.6),
  ('DSK Bank','Ипотечен кредит „Дом“',2,'https://dskbank.bg/individualni-klienti/krediti/ipotechni-krediti/ipotechen-kredit-dom',85,1,1,'Покупка на жилище',3.9),
  ('DSK Bank','Ипотечен кредит за рефинансиране',2,'https://dskbank.bg/individualni-klienti/krediti/ipotechni-krediti/refinansirane-na-ipotechen-kredit',85,1,1,'Рефинансиране',4.3),
  ('ОББ (UBB)','Ипотечен кредит „Дом“',2,'https://www.ubb.bg/individual-clients/krediti/ipotechni-krediti/ipotechen-kredit-dom',85,1,1,'Покупка на жилище',4.0),
  ('ОББ (UBB)','Зелен ипотечен кредит',2,'https://www.ubb.bg/individual-clients/krediti/ipotechni-krediti/zelen-ipotechen-kredit',80,1,1,'Енергийно ефективно жилище',3.6),
  ('UniCredit Bulbank','Ипотечен кредит „Класик“',2,'https://www.unicreditbulbank.bg/bg/individualni-klienti/krediti/ipotechni-krediti/ipotechen-kredit-klasik/',85,1,1,'Покупка/рефинансиране',4.2),
  ('UniCredit Bulbank','Ипотечен кредит „Плюс“',2,'https://www.unicreditbulbank.bg/bg/individualni-klienti/krediti/ipotechni-krediti/ipotechen-kredit-plus/',80,1,1,'Подобрения/ремонт',4.8),
  ('Fibank','Жилищен кредит „Дом“',2,'https://www.fibank.bg/bg/individualni-klienti/krediti/ipotechni-krediti/jilishten-kredit-dom',85,1,1,'Покупка на жилище',4.4),
  ('Fibank','Жилищен кредит за рефинансиране',2,'https://www.fibank.bg/bg/individualni-klienti/krediti/ipotechni-krediti/refinansirasht-jilishten-kredit',85,1,1,'Рефинансиране на жилищен кредит',4.9),
  ('Allianz Bank','Ипотечен кредит „Жилище“',2,'https://www.allianz.bg/bg_BG/individuals/banking/loans/ipotechen-kredit-za-jiliste.html',85,1,1,'Покупка на жилище',4.3),
  ('BACB','Ипотечен кредит „Дом“',2,'https://www.bacb.bg/bg/individualni-klienti/krediti/ipotechni-krediti/jilishten-kredit',85,1,1,'Покупка на жилище',4.7),
  ('CCB','Ипотечен кредит за жилище',2,'https://www.ccbank.bg/bg/fizicheski-lica/krediti/ipotechni-krediti/jilishten-kredit',85,1,1,'Покупка/строеж',4.5),
  ('Investbank','Ипотечен кредит „Стандарт“',2,'https://ibank.bg/bg/fizicheski-lica/krediti/ipotechni-krediti/ipotechen-kredit-standart',85,1,1,'Покупка/ремонт',4.6),
  ('IABank','Ипотечен кредит „Асет Дом“',2,'https://www.iabank.bg/fizicheski-lica/krediti/ipotechni-krediti/aset-dom',85,1,1,'Покупка на жилище',4.2),
  ('DBank','Ипотечен кредит „Д Банк Дом“',2,'https://www.dbank.bg/bg/individualni-klienti/krediti/ipotechen-kredit',85,1,1,'Покупка на жилище',4.4),
  ('Tokuda Bank','Ипотечен кредит „Токуда Дом“',2,'https://www.tokudabank.bg/bg/individual-clients/krediti/ipotechni-krediti',80,1,1,'Покупка на жилище',4.9);
";
        loan.ExecuteNonQuery();

        tx.Commit();
    }
}

// ---------------- Репозитории ----------------

public class DeposRepo
{
    private readonly IDbConnectionFactory _f;
    public DeposRepo(IDbConnectionFactory f) { _f = f; }

    public async Task<List<Deposit>> Search(
        string? bank = null,
        string? cur = null,
        int? minTerm = null,
        int? maxTerm = null,
        decimal? minAmt = null,
        decimal? maxRate = null)
    {
        using var con = _f.Create();
        ((dynamic)con).Open();

        using var cmd = con.CreateCommand();
        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(bank))
        {
            where.Add("b.Name LIKE @bank");
            var p = cmd.CreateParameter();
            p.ParameterName = "@bank";
            p.Value = "%" + bank + "%";
            cmd.Parameters.Add(p);
        }
        if (!string.IsNullOrWhiteSpace(cur))
        {
            where.Add("dp.Currency=@cur");
            var p = cmd.CreateParameter();
            p.ParameterName = "@cur";
            p.Value = cur;
            cmd.Parameters.Add(p);
        }
        if (minTerm.HasValue)
        {
            where.Add("dp.TermMonths>=@minTerm");
            var p = cmd.CreateParameter();
            p.ParameterName = "@minTerm";
            p.Value = minTerm.Value;
            cmd.Parameters.Add(p);
        }
        if (maxTerm.HasValue)
        {
            where.Add("dp.TermMonths<=@maxTerm");
            var p = cmd.CreateParameter();
            p.ParameterName = "@maxTerm";
            p.Value = maxTerm.Value;
            cmd.Parameters.Add(p);
        }
        if (minAmt.HasValue)
        {
            where.Add("dp.MinAmount<=@minAmt");
            var p = cmd.CreateParameter();
            p.ParameterName = "@minAmt";
            p.Value = minAmt.Value;
            cmd.Parameters.Add(p);
        }
        if (maxRate.HasValue)
        {
            where.Add("dp.RateAnnualPercent<=@maxRate");
            var p = cmd.CreateParameter();
            p.ParameterName = "@maxRate";
            p.Value = maxRate.Value;
            cmd.Parameters.Add(p);
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText = $@"
SELECT dp.Id,
       dp.BankId,
       b.Name,
       dp.Name,
       dp.Currency,
       dp.TermMonths,
       dp.MinAmount,
       dp.RateAnnualPercent,
       dp.DayCountBasis,
       dp.SourceUrl
FROM   DepositProduct dp
JOIN   Bank b ON b.Id = dp.BankId
{whereSql}
ORDER BY b.Name, dp.Name";

        var list = new List<Deposit>();
        using var r = ((dynamic)cmd).ExecuteReader();
        while (r.Read())
        {
            list.Add(new Deposit
            {
                Id = r.GetInt32(0),
                BankId = r.GetInt32(1),
                BankName = r.GetString(2),
                Name = r.GetString(3),
                Currency = r.GetString(4),
                TermMonths = r.GetInt32(5),
                MinAmount = r.GetDecimal(6),
                RateAnnualPercent = r.GetDecimal(7),
                DayCountBasis = r.IsDBNull(8) ? null : r.GetString(8),
                SourceUrl = r.IsDBNull(9) ? null : r.GetString(9)
            });
        }
        return list;
    }

    public async Task<List<Deposit>> ByIds(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        var res = new List<Deposit>();
        if (list.Count == 0) return res;

        using var con = _f.Create();
        ((dynamic)con).Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = $@"
SELECT dp.Id,
       dp.BankId,
       b.Name,
       dp.Name,
       dp.Currency,
       dp.TermMonths,
       dp.MinAmount,
       dp.RateAnnualPercent,
       dp.DayCountBasis,
       dp.SourceUrl
FROM   DepositProduct dp
JOIN   Bank b ON b.Id = dp.BankId
WHERE  dp.Id IN ({string.Join(',', list)})
";
        using var r = ((dynamic)cmd).ExecuteReader();
        while (r.Read())
        {
            res.Add(new Deposit
            {
                Id = r.GetInt32(0),
                BankId = r.GetInt32(1),
                BankName = r.GetString(2),
                Name = r.GetString(3),
                Currency = r.GetString(4),
                TermMonths = r.GetInt32(5),
                MinAmount = r.GetDecimal(6),
                RateAnnualPercent = r.GetDecimal(7),
                DayCountBasis = r.IsDBNull(8) ? null : r.GetString(8),
                SourceUrl = r.IsDBNull(9) ? null : r.GetString(9)
            });
        }
        return res;
    }

    public async Task<Deposit?> GetById(int id)
    {
        using var con = _f.Create();
        ((dynamic)con).Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = @"
SELECT dp.Id,
       dp.BankId,
       b.Name,
       dp.Name,
       dp.Currency,
       dp.TermMonths,
       dp.MinAmount,
       dp.RateAnnualPercent,
       dp.DayCountBasis,
       dp.SourceUrl
FROM   DepositProduct dp
JOIN   Bank b ON b.Id = dp.BankId
WHERE  dp.Id=@id";
        var p = cmd.CreateParameter();
        p.ParameterName = "@id";
        p.Value = id;
        cmd.Parameters.Add(p);

        using var r = ((dynamic)cmd).ExecuteReader();
        if (r.Read())
        {
            return new Deposit
            {
                Id = r.GetInt32(0),
                BankId = r.GetInt32(1),
                BankName = r.GetString(2),
                Name = r.GetString(3),
                Currency = r.GetString(4),
                TermMonths = r.GetInt32(5),
                MinAmount = r.GetDecimal(6),
                RateAnnualPercent = r.GetDecimal(7),
                DayCountBasis = r.IsDBNull(8) ? null : r.GetString(8),
                SourceUrl = r.IsDBNull(9) ? null : r.GetString(9)
            };
        }
        return null;
    }
}

public class LoansRepo
{
    private readonly IDbConnectionFactory _f;
    public LoansRepo(IDbConnectionFactory f) { _f = f; }

    public async Task<List<Loan>> All(int? type = null)
    {
        using var con = _f.Create();
        ((dynamic)con).Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText = type.HasValue
            ? "SELECT Id,Bank,Name,Type,SourceUrl,MaxLTVPercent,CollateralRequired,SalaryTransferRequired,Purpose,RepresentativeAPRPercent FROM LoanProduct WHERE Type=@t ORDER BY Bank,Name"
            : "SELECT Id,Bank,Name,Type,SourceUrl,MaxLTVPercent,CollateralRequired,SalaryTransferRequired,Purpose,RepresentativeAPRPercent FROM LoanProduct ORDER BY Bank,Name";

        if (type.HasValue)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = type.Value;
            cmd.Parameters.Add(p);
        }

        var res = new List<Loan>();
        using var r = ((dynamic)cmd).ExecuteReader();
        while (r.Read())
        {
            res.Add(new Loan
            {
                Id = r.GetInt32(0),
                Bank = r.GetString(1),
                Name = r.GetString(2),
                Type = r.GetInt32(3),
                SourceUrl = r.IsDBNull(4) ? null : r.GetString(4),
                MaxLTVPercent = r.IsDBNull(5) ? null : r.GetDecimal(5),
                CollateralRequired = r.IsDBNull(6) ? null : (r.GetInt32(6) != 0),
                SalaryTransferRequired = r.IsDBNull(7) ? null : (r.GetInt32(7) != 0),
                Purpose = r.IsDBNull(8) ? null : r.GetString(8),
                RepresentativeAPRPercent = r.IsDBNull(9) ? null : r.GetDecimal(9)
            });
        }
        return res;
    }

    public async Task<List<Loan>> ByIds(IEnumerable<int> ids)
    {
        var list = ids.Distinct().ToList();
        var outp = new List<Loan>();
        if (list.Count == 0) return outp;

        using var con = _f.Create();
        ((dynamic)con).Open();

        using var cmd = con.CreateCommand();
        cmd.CommandText =
            $"SELECT Id,Bank,Name,Type,SourceUrl,MaxLTVPercent,CollateralRequired,SalaryTransferRequired,Purpose,RepresentativeAPRPercent FROM LoanProduct WHERE Id IN ({string.Join(',', list)}) ORDER BY Bank,Name";

        using var r = ((dynamic)cmd).ExecuteReader();
        while (r.Read())
        {
            outp.Add(new Loan
            {
                Id = r.GetInt32(0),
                Bank = r.GetString(1),
                Name = r.GetString(2),
                Type = r.GetInt32(3),
                SourceUrl = r.IsDBNull(4) ? null : r.GetString(4),
                MaxLTVPercent = r.IsDBNull(5) ? null : r.GetDecimal(5),
                CollateralRequired = r.IsDBNull(6) ? null : (r.GetInt32(6) != 0),
                SalaryTransferRequired = r.IsDBNull(7) ? null : (r.GetInt32(7) != 0),
                Purpose = r.IsDBNull(8) ? null : r.GetString(8),
                RepresentativeAPRPercent = r.IsDBNull(9) ? null : r.GetDecimal(9)
            });
        }
        return outp;
    }
}
