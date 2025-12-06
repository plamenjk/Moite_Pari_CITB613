//BankProducts.Core/Services.cs
namespace BankProducts.Core;

public class DepositCalcService
{
  public (decimal gross, decimal tax, decimal net, decimal eirPct) Calc(Deposit p, decimal amount, bool netAfterTax, decimal taxRatePercent)
  {
    if (amount < p.MinAmount) throw new InvalidOperationException($"Минималната сума е {p.MinAmount:n2} {p.Currency}.");
    decimal r = p.RateAnnualPercent / 100m; int months = p.TermMonths; decimal gross = Math.Round(amount * r * (months / 12m), 2);
    decimal tax = netAfterTax ? Math.Round(gross * (taxRatePercent / 100m), 2) : 0m; decimal net = gross - tax;
    var years = Math.Max(1m / 12m, months / 12m);
    double eir = Math.Pow((double)((amount + (netAfterTax ? net : gross)) / amount), 1.0 / (double)years) - 1.0;
    return (gross, tax, net, (decimal)Math.Round(eir * 100.0, 3));
  }
}

public class LoanCalcService
{
  public LoanRow Calc(Loan p, LoanInput i)
  {
    double r = (double)(i.AnnualRatePercent / 100m) / 12.0; int n = i.TermMonths; double P = (double)i.Principal; double mfee = (double)i.MonthlyFee;
    double annuity = r == 0 ? P / n : (P * r) / (1 - Math.Pow(1 + r, -n)); double payment = annuity + mfee;
    double bal = P, totalPaid = 0, totalInt = 0; var sch = new List<(int, decimal, decimal, decimal)>();
    for (int k = 1; k <= n; k++) { double interest = bal * r; double principal = payment - interest - mfee; if (principal < 0) principal = 0; bal -= principal; if (bal < 1e-6) bal = 0; totalPaid += payment; totalInt += interest; sch.Add((k, (decimal)Math.Round(payment, 2), (decimal)Math.Round(interest, 2), (decimal)Math.Round(bal, 2))); }
    double cash0 = (double)i.Principal - (double)i.FeesUpfront; Func<double, double> npv = (rate) => { double v = cash0; double disc = 1.0; for (int k = 1; k <= n; k++) { disc *= (1.0 + rate); v -= payment / disc; } return v; };
    double lo = 0.0, hi = 1.0; for (int it = 0; it < 80; it++) { double mid = (lo + hi) / 2.0; double f = npv(mid); double flo = npv(lo); if (Math.Abs(f) < 1e-10) { lo = hi = mid; break; } if (Math.Sign(f) == Math.Sign(flo)) lo = mid; else hi = mid; }
    double aprMonthly = (lo + hi) / 2.0; double aprAnnual = Math.Pow(1.0 + aprMonthly, 12.0) - 1.0;
    return new LoanRow { Product = p, Input = i, MonthlyPayment = (decimal)Math.Round(payment, 2), APRPercent = (decimal)Math.Round(aprAnnual * 100.0, 3), TotalPaid = (decimal)Math.Round(totalPaid, 2), TotalInterest = (decimal)Math.Round(totalInt, 2), Schedule = sch };
  }
}

