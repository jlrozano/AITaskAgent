using System.Globalization;
using BRMS.Core.Constants;

namespace BRMS.StdRules.Constants;

/// <summary>
/// Manages access to consolidated resources for BRMS.StdRules.
/// Provides centralized access to descriptions and log messages with localization support.
/// </summary>
public static class ResourcesKeys
{
    /// <summary>
    /// Gets a localized string resource by key.
    /// </summary>
    /// <param name="key">The resource key to retrieve</param>
    /// <returns>The localized string value, or the key itself if not found</returns>
    public static string GetString(string key)
    {
        try
        {
            string value = ResourcesManager.GetLocalizedMessage(key); //, CultureInfo.CurrentUICulture);
            return value ?? key;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>
    /// Gets a localized string resource by key with specific culture.
    /// </summary>
    /// <param name="key">The resource key to retrieve</param>
    /// <param name="culture">The culture to use for localization</param>
    /// <returns>The localized string value, or the key itself if not found</returns>
    public static string GetString(string key, CultureInfo culture)
    {
        try
        {
            string value = ResourcesManager.GetLocalizedMessage(key, culture);
            return value ?? key;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>
    /// Gets a formatted localized string resource by key with arguments.
    /// </summary>
    /// <param name="key">The resource key to retrieve</param>
    /// <param name="args">Arguments to format into the string</param>
    /// <returns>The formatted localized string value, or the key itself if not found</returns>
    public static string GetFormattedString(string key, params object[] args)
    {
        try
        {
            string format = ResourcesManager.GetLocalizedMessage(key);
            return args?.Length > 0 ? string.Format(format, args) : format;
        }
        catch
        {
            return key;
        }
    }

    // ===== DESCRIPTION RESOURCE KEYS =====

    // Normalizer Description Keys
    public const string Desc_DefaultValueNormalizer_Description = "Desc_DefaultValueNormalizer_Description";
    public const string Desc_DefaultValueNormalizer_DefaultValue_Description = "Desc_DefaultValueNormalizer_DefaultValue_Description";
    public const string Desc_TrimSpacesNormalizer_Description = "Desc_TrimSpacesNormalizer_Description";
    public const string Desc_NullIfEmptyNormalizer_Description = "Desc_NullIfEmptyNormalizer_Description";
    public const string Desc_ToUpperNormalizer_Description = "Desc_ToUpperNormalizer_Description";
    public const string Desc_HashCharNormalizer_Description = "Desc_HashCharNormalizer_Description";
    public const string Desc_AdvancedCleanNormalizer_Description = "Desc_AdvancedCleanNormalizer_Description";
    public const string Desc_JsScriptNormalizer_Description = "Desc_JsScriptNormalizer_Description";
    public const string Desc_JsScriptNormalizer_NotifyChanges = "Desc_JsScriptNormalizer_NotifyChanges";
    public const string Desc_HpptNormalizer_Url_Description = "Desc_HpptNormalizer_Url_Description";
    public const string Desc_HpptNormalizer_Headers_Description = "Desc_HpptNormalizer_Headers_Description";
    public const string Desc_HpptNormalizer_Description = "Desc_HpptNormalizer_Description";
    public const string Desc_ToLowerNormalizer_Description = "Desc_ToLowerNormalizer_Description";
    public const string Desc_CollapseSpacesNormalizer_Description = "Desc_CollapseSpacesNormalizer_Description";
    public const string Desc_ToTitleCaseNormalizer_Description = "Desc_ToTitleCaseNormalizer_Description";
    public const string Desc_ToTitleCaseNormalizer_Culture_Description = "Desc_ToTitleCaseNormalizer_Culture_Description";
    public const string Desc_E164PhoneNormalizer_Description = "Desc_E164PhoneNormalizer_Description";
    public const string Desc_E164PhoneNormalizer_RegionIso_Description = "Desc_E164PhoneNormalizer_RegionIso_Description";
    public const string Desc_RemoveDiacriticsNormalizer_Description = "Desc_RemoveDiacriticsNormalizer_Description";
    public const string Desc_RemoveDiacriticsNormalizer_PreserveEnie_Description = "Desc_RemoveDiacriticsNormalizer_PreserveEnie_Description";
    // Validator Description Keys
    public const string Desc_RequiredValidator_Description = "Desc_RequiredValidator_Description";
    public const string Desc_HpptValidator_Url_Description = "Desc_HpptValidator_Url_Description";
    public const string Desc_HpptValidator_Headers_Description = "Desc_HpptValidator_Headers_Description";
    public const string Desc_RequiredFieldValidator_Description = "Desc_RequiredFieldValidator_Description";
    public const string Desc_TextLengthValidator_Description = "Desc_TextLengthValidator_Description";
    public const string Desc_TextLengthValidator_MinLength_Description = "Desc_TextLengthValidator_MinLength_Description";
    public const string Desc_TextLengthValidator_MaxLength_Description = "Desc_TextLengthValidator_MaxLength_Description";
    public const string Desc_CountValidator_Description = "Desc_CountValidator_Description";
    public const string Desc_CountValidator_MinCount_Description = "Desc_CountValidator_MinCount_Description";
    public const string Desc_CountValidator_MaxCount_Description = "Desc_CountValidator_MaxCount_Description";
    public const string Desc_RangeDecimalValidator_Description = "Desc_RangeDecimalValidator_Description";
    public const string Desc_RangeDecimalValidator_MinValue_Description = "Desc_RangeDecimalValidator_MinValue_Description";
    public const string Desc_RangeDecimalValidator_MaxValue_Description = "Desc_RangeDecimalValidator_MaxValue_Description";
    public const string Desc_RangeNumberValidator_Description = "Desc_RangeNumberValidator_Description";
    public const string Desc_RangeNumberValidator_MinValue_Description = "Desc_RangeNumberValidator_MinValue_Description";
    public const string Desc_RangeNumberValidator_MaxValue_Description = "Desc_RangeNumberValidator_MaxValue_Description";
    public const string Desc_RangeDateValidator_Description = "Desc_RangeDateValidator_Description";
    public const string Desc_RangeDateValidator_MinValue_Description = "Desc_RangeDateValidator_MinValue_Description";
    public const string Desc_RangeDateValidator_MaxValue_Description = "Desc_RangeDateValidator_MaxValue_Description";
    public const string Desc_InvalidDateValidator_Description = "Desc_InvalidDateValidator_Description";
    public const string Desc_NullOrMinus99IntValidator_Description = "Desc_NullOrMinus99IntValidator_Description";
    public const string Desc_NullOrEmptyStringValidator_Description = "Desc_NullOrEmptyStringValidator_Description";
    public const string Desc_ListValidator_Description = "Desc_ListValidator_Description";
    public const string Desc_ListValidator_AllowedValues_Description = "Desc_ListValidator_AllowedValues_Description";
    public const string Desc_ListValidator_ForbiddenValues_Description = "Desc_ListValidator_ForbiddenValues_Description";
    public const string Desc_RegexValidator_Description = "Desc_RegexValidator_Description";
    public const string Desc_RegexValidator_Pattern = "Desc_RegexValidator_Pattern";
    public const string Desc_JsScriptValidator_Description = "Desc_JsScriptValidator_Description";
    public const string Desc_EmailValidator_Description = "Desc_EmailValidator_Description";
    public const string Desc_EmailValidator_Strict_Description = "Desc_EmailValidator_Strict_Description";
    public const string Desc_PhoneValidator_Description = "Desc_PhoneValidator_Description";
    public const string Desc_PhoneValidator_RegionIso_Description = "Desc_PhoneValidator_RegionIso_Description";
    public const string Desc_PhoneValidator_Strict_Description = "Desc_PhoneValidator_Strict_Description";

    // Common Validator Property Description Keys
    public const string Desc_Validator_AllowNull_Description = "Desc_Validator_AllowNull_Description";

    // Database Components Description Keys
    public const string Desc_DatabaseValidator_Description = "Desc_DatabaseValidator_Description";
    public const string Desc_DatabaseValidator_SQLSmtp_Description = "Desc_DatabaseValidator_SQLSmtp_Description";
    public const string Desc_DatabaseNormalizer_Description = "Desc_DatabaseNormalizer_Description";
    public const string Desc_DatabaseNormalizer_SQLSmtp_Description = "Desc_DatabaseNormalizer_SQLSmtp_Description";
    public const string Desc_DatabaseTranslator_Description = "Desc_DatabaseTranslator_Description";
    public const string Desc_DatabaseTranslator_SQLSmtp_Description = "Desc_DatabaseTranslator_SQLSmtp_Description";
    public const string Desc_Database_DataBaseName_Description = "Desc_Database_DataBaseName_Description";

    // JavaScript Rule Description Keys
    public const string Desc_JsScriptRule_Description = "Desc_JsScriptRule_Description";
    public const string Desc_JsScriptRule_Expression = "Desc_JsScriptRule_Expression";
    public const string Desc_JsScriptRule_Name = "Desc_JsScriptRule_Name";
    public const string Desc_JsScriptRule_ErrorMessage = "Desc_JsScriptRule_ErrorMessage";
    public const string Desc_JsScriptRule_ErrorSeverityLevel = "Desc_JsScriptRule_ErrorSeverityLevel";
    public const string Desc_JsScriptRule_RuleName_Description = "Desc_JsScriptRule_RuleName_Description";
    public const string Desc_JsScriptRule_Expression_Description = "Desc_JsScriptRule_Expression_Description";
    public const string Desc_JSTransformation_Description = "Desc_JSTransformation_Description";
    public const string Desc_JSTransformation_OutputType_Description = "Desc_JSTransformation_OutputType_Description";

    // Plugin Description Keys
    public const string Desc_PluginRule_Description = "Desc_PluginRule_Description";
    public const string Desc_PluginRule_PluginName_Description = "Desc_PluginRule_PluginName_Description";
    public const string Desc_PluginRule_MethodName_Description = "Desc_PluginRule_MethodName_Description";
    public const string Desc_StdRulesPlugin_Description = "Desc_StdRulesPlugin_Description";

    // ===== LOG MESSAGE RESOURCE KEYS =====

    // AdvancedCleanNormalizer Log Keys
    public const string Log_AdvancedCleanNormalizer_Starting = "Log_AdvancedCleanNormalizer_Starting";
    public const string Log_AdvancedCleanNormalizer_Processing = "Log_AdvancedCleanNormalizer_Processing";
    public const string Log_AdvancedCleanNormalizer_Success = "Log_AdvancedCleanNormalizer_Success";

    // TrimSpacesNormalizer Log Keys
    public const string Log_TrimSpacesNormalizer_Starting = "Log_TrimSpacesNormalizer_Starting";
    public const string Log_TrimSpacesNormalizer_Processing = "Log_TrimSpacesNormalizer_Processing";
    public const string Log_TrimSpacesNormalizer_Success = "Log_TrimSpacesNormalizer_Success";
    public const string Log_TrimSpacesNormalizer_Error = "Log_TrimSpacesNormalizer_Error";

    // NullIfEmptyNormalizer Log Keys
    public const string Log_NullIfEmptyNormalizer_Starting = "Log_NullIfEmptyNormalizer_Starting";
    public const string Log_NullIfEmptyNormalizer_Processing = "Log_NullIfEmptyNormalizer_Processing";
    public const string Log_NullIfEmptyNormalizer_Success = "Log_NullIfEmptyNormalizer_Success";
    public const string Log_NullIfEmptyNormalizer_Error = "Log_NullIfEmptyNormalizer_Error";

    // ToUpperNormalizer Log Keys
    public const string Log_ToUpperNormalizer_Starting = "Log_ToUpperNormalizer_Starting";
    public const string Log_ToUpperNormalizer_Processing = "Log_ToUpperNormalizer_Processing";
    public const string Log_ToUpperNormalizer_Success = "Log_ToUpperNormalizer_Success";
    public const string Log_ToUpperNormalizer_Error = "Log_ToUpperNormalizer_Error";

    // HashCharNormalizer Log Keys
    public const string Log_HashCharNormalizer_Starting = "Log_HashCharNormalizer_Starting";
    public const string Log_HashCharNormalizer_Processing = "Log_HashCharNormalizer_Processing";
    public const string Log_HashCharNormalizer_Success = "Log_HashCharNormalizer_Success";
    public const string Log_HashCharNormalizer_Error = "Log_HashCharNormalizer_Error";

    // DefaultValueNormalizer Log Keys
    public const string Log_DefaultValueNormalizer_Starting = "Log_DefaultValueNormalizer_Starting";
    public const string Log_DefaultValueNormalizer_Processing = "Log_DefaultValueNormalizer_Processing";
    public const string Log_DefaultValueNormalizer_Success = "Log_DefaultValueNormalizer_Success";
    public const string Log_DefaultValueNormalizer_Error = "Log_DefaultValueNormalizer_Error";

    // CountValidator Log Keys
    public const string Log_CountValidator_Starting = "Log_CountValidator_Starting";
    public const string Log_CountValidator_Processing = "Log_CountValidator_Processing";
    public const string Log_CountValidator_Success = "Log_CountValidator_Success";
    public const string Log_CountValidator_Error = "Log_CountValidator_Error";

    // NullOrEmptyStringValidator Log Keys
    public const string Log_NullOrEmptyStringValidator_Starting = "Log_NullOrEmptyStringValidator_Starting";
    public const string Log_NullOrEmptyStringValidator_Processing = "Log_NullOrEmptyStringValidator_Processing";
    public const string Log_NullOrEmptyStringValidator_Success = "Log_NullOrEmptyStringValidator_Success";
    public const string Log_NullOrEmptyStringValidator_Error = "Log_NullOrEmptyStringValidator_Error";

    // RequiredFieldValidator Log Keys
    public const string Log_RequiredFieldValidator_Starting = "Log_RequiredFieldValidator_Starting";
    public const string Log_RequiredFieldValidator_Processing = "Log_RequiredFieldValidator_Processing";
    public const string Log_RequiredFieldValidator_Success = "Log_RequiredFieldValidator_Success";
    public const string Log_RequiredFieldValidator_Error = "Log_RequiredFieldValidator_Error";

    // RangeDecimalValidator Log Keys
    public const string Log_RangeDecimalValidator_Starting = "Log_RangeDecimalValidator_Starting";
    public const string Log_RangeDecimalValidator_Processing = "Log_RangeDecimalValidator_Processing";
    public const string Log_RangeDecimalValidator_Success = "Log_RangeDecimalValidator_Success";
    public const string Log_RangeDecimalValidator_Error = "Log_RangeDecimalValidator_Error";

    // RangeDateValidator Log Keys
    public const string Log_RangeDateValidator_Starting = "Log_RangeDateValidator_Starting";
    public const string Log_RangeDateValidator_Processing = "Log_RangeDateValidator_Processing";
    public const string Log_RangeDateValidator_Success = "Log_RangeDateValidator_Success";
    public const string Log_RangeDateValidator_Error = "Log_RangeDateValidator_Error";

    // InvalidDateValidator Log Keys
    public const string Log_InvalidDateValidator_Starting = "Log_InvalidDateValidator_Starting";
    public const string Log_InvalidDateValidator_Processing = "Log_InvalidDateValidator_Processing";
    public const string Log_InvalidDateValidator_Success = "Log_InvalidDateValidator_Success";
    public const string Log_InvalidDateValidator_Error = "Log_InvalidDateValidator_Error";

    // NullOrMinus99IntValidator Log Keys
    public const string Log_NullOrMinus99IntValidator_Starting = "Log_NullOrMinus99IntValidator_Starting";
    public const string Log_NullOrMinus99IntValidator_Processing = "Log_NullOrMinus99IntValidator_Processing";
    public const string Log_NullOrMinus99IntValidator_Success = "Log_NullOrMinus99IntValidator_Success";
    public const string Log_NullOrMinus99IntValidator_Error = "Log_NullOrMinus99IntValidator_Error";

    // RegexValidator Log Keys
    public const string Log_RegexValidator_Starting = "Log_RegexValidator_Starting";
    public const string Log_RegexValidator_Processing = "Log_RegexValidator_Processing";
    public const string Log_RegexValidator_Success = "Log_RegexValidator_Success";
    public const string Log_RegexValidator_Error = "Log_RegexValidator_Error";

    // RangeNumberValidator Log Keys
    public const string Log_RangeNumberValidator_Starting = "Log_RangeNumberValidator_Starting";
    public const string Log_RangeNumberValidator_Processing = "Log_RangeNumberValidator_Processing";
    public const string Log_RangeNumberValidator_Success = "Log_RangeNumberValidator_Success";
    public const string Log_RangeNumberValidator_Error = "Log_RangeNumberValidator_Error";

    // ListValidator Log Keys
    public const string Log_ListValidator_Starting = "Log_ListValidator_Starting";
    public const string Log_ListValidator_Processing = "Log_ListValidator_Processing";
    public const string Log_ListValidator_NotInAllowed = "Log_ListValidator_NotInAllowed";
    public const string Log_ListValidator_InForbidden = "Log_ListValidator_InForbidden";
    public const string Log_ListValidator_ValidValue = "Log_ListValidator_ValidValue";
    public const string Log_ListValidator_NullValue = "Log_ListValidator_NullValue";
    public const string Log_ListValidator_Error = "Log_ListValidator_Error";

    // Database Components Log Keys
    public const string Log_DatabaseValidator_Starting = "Log_DatabaseValidator_Starting";
    public const string Log_DatabaseValidator_Processing = "Log_DatabaseValidator_Processing";
    public const string Log_DatabaseValidator_Success = "Log_DatabaseValidator_Success";
    public const string Log_DatabaseValidator_Error = "Log_DatabaseValidator_Error";

    public const string Log_DatabaseNormalizer_Starting = "Log_DatabaseNormalizer_Starting";
    public const string Log_DatabaseNormalizer_Processing = "Log_DatabaseNormalizer_Processing";
    public const string Log_DatabaseNormalizer_Success = "Log_DatabaseNormalizer_Success";
    public const string Log_DatabaseNormalizer_Error = "Log_DatabaseNormalizer_Error";

    public const string Log_DatabaseTranslator_Starting = "Log_DatabaseTranslator_Starting";
    public const string Log_DatabaseTranslator_Processing = "Log_DatabaseTranslator_Processing";
    public const string Log_DatabaseTranslator_Success = "Log_DatabaseTranslator_Success";
    public const string Log_DatabaseTranslator_Error = "Log_DatabaseTranslator_Error";
}
