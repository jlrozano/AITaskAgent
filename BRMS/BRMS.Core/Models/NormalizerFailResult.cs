using BRMS.Core.Abstractions;

namespace BRMS.Core.Models;

public class NormalizerFailResult(INormalizer rule, BRMSExecutionContext context, string errorMessage) :
    NormalizerResult(rule, context, string.IsNullOrWhiteSpace(errorMessage) ? "Error" : errorMessage);
