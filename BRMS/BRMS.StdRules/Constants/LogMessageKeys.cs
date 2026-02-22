namespace BRMS.StdRules.Constants;

/// <summary>
/// Constantes estáticas para las claves de mensajes de logging utilizadas en BRMS.StdRules
/// </summary>
public static class LogMessageKeys
{
    #region Normalizers

    // AdvancedCleanNormalizer
    public const string AdvancedCleanNormalizerStarting = "AdvancedCleanNormalizer_Starting";
    public const string AdvancedCleanNormalizerProcessing = "AdvancedCleanNormalizer_Processing";
    public const string AdvancedCleanNormalizerSuccess = "AdvancedCleanNormalizer_Success";
    public const string AdvancedCleanNormalizerError = "AdvancedCleanNormalizer_Error";

    // DefaultValueNormalizer
    public const string DefaultValueNormalizerStarting = "DefaultValueNormalizer_Starting";
    public const string DefaultValueNormalizerProcessing = "DefaultValueNormalizer_Processing";
    public const string DefaultValueNormalizerSuccess = "DefaultValueNormalizer_Success";
    public const string DefaultValueNormalizerError = "DefaultValueNormalizer_Error";

    // HashCharNormalizer
    public const string HashCharNormalizerStarting = "HashCharNormalizer_Starting";
    public const string HashCharNormalizerProcessing = "HashCharNormalizer_Processing";
    public const string HashCharNormalizerSuccess = "HashCharNormalizer_Success";
    public const string HashCharNormalizerError = "HashCharNormalizer_Error";

    // NullIfEmptyNormalizer
    public const string NullIfEmptyNormalizerStarting = "NullIfEmptyNormalizer_Starting";
    public const string NullIfEmptyNormalizerProcessing = "NullIfEmptyNormalizer_Processing";
    public const string NullIfEmptyNormalizerSuccess = "NullIfEmptyNormalizer_Success";
    public const string NullIfEmptyNormalizerError = "NullIfEmptyNormalizer_Error";

    // ToUpperNormalizer
    public const string ToUpperNormalizerStarting = "ToUpperNormalizer_Starting";
    public const string ToUpperNormalizerProcessing = "ToUpperNormalizer_Processing";
    public const string ToUpperNormalizerSuccess = "ToUpperNormalizer_Success";
    public const string ToUpperNormalizerError = "ToUpperNormalizer_Error";

    // TrimSpacesNormalizer
    public const string TrimSpacesNormalizerStarting = "TrimSpacesNormalizer_Starting";
    public const string TrimSpacesNormalizerProcessing = "TrimSpacesNormalizer_Processing";
    public const string TrimSpacesNormalizerSuccess = "TrimSpacesNormalizer_Success";
    public const string TrimSpacesNormalizerError = "TrimSpacesNormalizer_Error";

    // HpptNormalizer
    public const string HpptNormalizerStarting = "HpptNormalizer_Starting";
    public const string HpptNormalizerProcessing = "HpptNormalizer_Processing";
    public const string HpptNormalizerSuccess = "HpptNormalizer_Success";
    public const string HpptNormalizerError = "HpptNormalizer_Error";

    #endregion

    #region Validators

    // HpptValidator
    public const string HpptValidatorStarting = "HpptValidator_Starting";
    public const string HpptValidatorProcessing = "HpptValidator_Processing";
    public const string HpptValidatorSuccess = "HpptValidator_Success";
    public const string HpptValidatorError = "HpptValidator_Error";

    // CountValidator
    public const string CountValidatorStarting = "CountValidator_Starting";
    public const string CountValidatorProcessing = "CountValidator_Processing";
    public const string CountValidatorSuccess = "CountValidator_Success";
    public const string CountValidatorError = "CountValidator_Error";

    // InvalidDateValidator
    public const string InvalidDateValidatorStarting = "InvalidDateValidator_Starting";
    public const string InvalidDateValidatorProcessing = "InvalidDateValidator_Processing";
    public const string InvalidDateValidatorFailed = "InvalidDateValidator_Failed";
    public const string InvalidDateValidatorSuccess = "InvalidDateValidator_Success";
    public const string InvalidDateValidatorError = "InvalidDateValidator_Error";

    // ListValidator
    public const string ListValidatorStarting = "ListValidator_Starting";
    public const string ListValidatorProcessing = "ListValidator_Processing";
    public const string ListValidatorNotInAllowed = "ListValidator_NotInAllowed";
    public const string ListValidatorInForbidden = "ListValidator_InForbidden";
    public const string ListValidatorValidValue = "ListValidator_ValidValue";
    public const string ListValidatorNullValue = "ListValidator_NullValue";
    public const string ListValidatorError = "ListValidator_Error";

    // NullOrEmptyStringValidator
    public const string NullOrEmptyStringValidatorStarting = "NullOrEmptyStringValidator_Starting";
    public const string NullOrEmptyStringValidatorProcessing = "NullOrEmptyStringValidator_Processing";
    public const string NullOrEmptyStringValidatorSuccess = "NullOrEmptyStringValidator_Success";
    public const string NullOrEmptyStringValidatorError = "NullOrEmptyStringValidator_Error";

    // NullOrMinus99IntValidator
    public const string NullOrMinus99IntValidatorStarting = "NullOrMinus99IntValidator_Starting";
    public const string NullOrMinus99IntValidatorProcessing = "NullOrMinus99IntValidator_Processing";
    public const string NullOrMinus99IntValidatorFailed = "NullOrMinus99IntValidator_Failed";
    public const string NullOrMinus99IntValidatorSuccess = "NullOrMinus99IntValidator_Success";
    public const string NullOrMinus99IntValidatorError = "NullOrMinus99IntValidator_Error";

    // RangeDateValidator
    public const string RangeDateValidatorStarting = "RangeDateValidator_Starting";
    public const string RangeDateValidatorNullValue = "RangeDateValidator_NullValue";
    public const string RangeDateValidatorInvalidDate = "RangeDateValidator_InvalidDate";
    public const string RangeDateValidatorProcessing = "RangeDateValidator_Processing";
    public const string RangeDateValidatorBelowMin = "RangeDateValidator_BelowMin";
    public const string RangeDateValidatorAboveMax = "RangeDateValidator_AboveMax";
    public const string RangeDateValidatorSuccess = "RangeDateValidator_Success";
    public const string RangeDateValidatorError = "RangeDateValidator_Error";

    // RangeDecimalValidator
    public const string RangeDecimalValidatorStarting = "RangeDecimalValidator_Starting";
    public const string RangeDecimalValidatorProcessing = "RangeDecimalValidator_Processing";
    public const string RangeDecimalValidatorSuccess = "RangeDecimalValidator_Success";
    public const string RangeDecimalValidatorError = "RangeDecimalValidator_Error";

    // RangeNumberValidator
    public const string RangeNumberValidatorStarting = "RangeNumberValidator_Starting";
    public const string RangeNumberValidatorNullValue = "RangeNumberValidator_NullValue";
    public const string RangeNumberValidatorProcessing = "RangeNumberValidator_Processing";
    public const string RangeNumberValidatorInvalidNumber = "RangeNumberValidator_InvalidNumber";
    public const string RangeNumberValidatorBelowMin = "RangeNumberValidator_BelowMin";
    public const string RangeNumberValidatorAboveMax = "RangeNumberValidator_AboveMax";
    public const string RangeNumberValidatorSuccess = "RangeNumberValidator_Success";
    public const string RangeNumberValidatorError = "RangeNumberValidator_Error";

    // RegexValidator
    public const string RegexValidatorStarting = "RegexValidator_Starting";
    public const string RegexValidatorProcessing = "RegexValidator_Processing";
    public const string RegexValidatorFailed = "RegexValidator_Failed";
    public const string RegexValidatorSuccess = "RegexValidator_Success";
    public const string RegexValidatorError = "RegexValidator_Error";

    // RequiredFieldValidator
    public const string RequiredFieldValidatorStarting = "RequiredFieldValidator_Starting";
    public const string RequiredFieldValidatorProcessing = "RequiredFieldValidator_Processing";
    public const string RequiredFieldValidatorSuccess = "RequiredFieldValidator_Success";
    public const string RequiredFieldValidatorError = "RequiredFieldValidator_Error";

    #endregion

    // EmailValidator Log Keys
    public const string LogEmailValidatorStarting = "Log_EmailValidator_Starting";
    public const string LogEmailValidatorProcessing = "Log_EmailValidator_Processing";
    public const string LogEmailValidatorSuccess = "Log_EmailValidator_Success";
    public const string LogEmailValidatorError = "Log_EmailValidator_Error";

    // PhoneValidator Log Keys
    public const string LogPhoneValidatorStarting = "Log_PhoneValidator_Starting";
    public const string LogPhoneValidatorProcessing = "Log_PhoneValidator_Processing";
    public const string LogPhoneValidatorSuccess = "Log_PhoneValidator_Success";
    public const string LogPhoneValidatorError = "Log_PhoneValidator_Error";

    #region Transformations

    // HpptTransformation
    public const string HpptTransformationStarting = "HpptTransformation_Starting";
    public const string HpptTransformationProcessing = "HpptTransformation_Processing";
    public const string HpptTransformationSuccess = "HpptTransformation_Success";
    public const string HpptTransformationError = "HpptTransformation_Error";

    #endregion
}
