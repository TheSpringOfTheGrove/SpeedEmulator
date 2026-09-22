using SpeedEmulator.Models;
using SpeedEmulator.Repositories;

namespace SpeedEmulator.Services;

public sealed record FlowRecordSaveResult(
    int SavedCount,
    FlowFormulaEvaluationSummary FormulaSummary);

public sealed class FormulaEvaluationException : InvalidOperationException
{
    public FormulaEvaluationException(IReadOnlyList<FormulaDiagnostic> diagnostics)
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<FormulaDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IReadOnlyList<FormulaDiagnostic> diagnostics)
    {
        var errors = diagnostics
            .Where(item => item.Severity == FormulaDiagnosticSeverity.Error)
            .Take(5)
            .Select(item => $"第 {item.RecordIndex} 行/{item.ColumnName}：{item.Message}")
            .ToList();
        return errors.Count == 0
            ? "公式转换失败。"
            : "公式转换失败：" + string.Join("；", errors);
    }
}

public interface IFlowRecordSaveService
{
    Task<FlowRecordSaveResult> SaveAllAsync(
        Bank bank,
        long bankUserId,
        IEnumerable<FlowRecord> records);

    FlowFormulaEvaluationSummary ConvertInPlace(
        Bank bank,
        IEnumerable<FlowRecord> records);
}

public sealed class FlowRecordSaveService : IFlowRecordSaveService
{
    private readonly IFlowRecordRepository repository;
    private readonly IFlowFormulaEvaluator formulaEvaluator;
    private readonly IFormulaDataSetService formulaDataSetService;

    public FlowRecordSaveService(
        IFlowRecordRepository repository,
        IFlowFormulaEvaluator formulaEvaluator,
        IFormulaDataSetService formulaDataSetService)
    {
        this.repository = repository;
        this.formulaEvaluator = formulaEvaluator;
        this.formulaDataSetService = formulaDataSetService;
    }

    public async Task<FlowRecordSaveResult> SaveAllAsync(
        Bank bank,
        long bankUserId,
        IEnumerable<FlowRecord> records)
    {
        var source = records.ToList();
        var evaluated = source.Select(item => item.Clone()).ToList();
        var summary = Evaluate(bank, evaluated);
        ThrowIfInvalid(summary);

        await repository.SaveAllAsync(bank.Id, bankUserId, evaluated);
        for (var index = 0; index < source.Count; index++)
        {
            FlowFormulaEvaluator.CopyEvaluatedValues(bank, evaluated[index], source[index]);
        }

        return new FlowRecordSaveResult(source.Count, summary);
    }

    public FlowFormulaEvaluationSummary ConvertInPlace(
        Bank bank,
        IEnumerable<FlowRecord> records)
    {
        var source = records.ToList();
        var evaluated = source.Select(item => item.Clone()).ToList();
        var summary = Evaluate(bank, evaluated);
        ThrowIfInvalid(summary);

        for (var index = 0; index < source.Count; index++)
        {
            FlowFormulaEvaluator.CopyEvaluatedValues(bank, evaluated[index], source[index]);
        }

        return summary;
    }

    private FlowFormulaEvaluationSummary Evaluate(Bank bank, IReadOnlyList<FlowRecord> records)
    {
        return formulaEvaluator.Evaluate(bank, records, formulaDataSetService.Get(bank.Id));
    }

    private static void ThrowIfInvalid(FlowFormulaEvaluationSummary summary)
    {
        if (summary.HasErrors)
        {
            throw new FormulaEvaluationException(summary.Diagnostics);
        }
    }
}
