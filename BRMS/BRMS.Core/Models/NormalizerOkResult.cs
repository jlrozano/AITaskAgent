using BRMS.Core.Abstractions;

namespace BRMS.Core.Models;

public class NormalizerOkResult(INormalizer rule, BRMSExecutionContext context, bool? hasChanges = null) : NormalizerResult(rule, context, null, hasChanges);
