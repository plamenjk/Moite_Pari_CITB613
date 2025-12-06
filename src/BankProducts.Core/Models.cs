namespace BankProducts.Core;

public record Deposit{
  public int Id;
  public int BankId;
  public string BankName = "";
  public string Name = "";
  public string Currency = "BGN";
  public int TermMonths;
  public decimal MinAmount;
  public decimal RateAnnualPercent;
  public string? DayCountBasis;
  public string? SourceUrl;
}

/// <summary>Type: 1=Consumer, 2=Mortgage</summary>
public record Loan{
  public int Id;
  public string Bank = "";
  public string Name = "";
  public int Type;
  public string? SourceUrl;
  public decimal? MaxLTVPercent;
  public bool? CollateralRequired;
  public bool? SalaryTransferRequired;
  public string? Purpose;

  // НОВО: представителен ГПР от сайта на банката
  public decimal? RepresentativeAPRPercent;
}

public record LoanInput{
  public int ProductId;
  public decimal Principal;
  public int TermMonths;
  public decimal AnnualRatePercent;
  public decimal FeesUpfront;
  public decimal MonthlyFee;
  public DateTime StartDate = DateTime.Today;
}

public record LoanRow{
  public Loan Product = new();
  public LoanInput Input = new();
  public decimal MonthlyPayment;
  public decimal APRPercent;
  public decimal TotalPaid;
  public decimal TotalInterest;
  public List<(int n, decimal pay, decimal interest, decimal balance)> Schedule = new();
}
