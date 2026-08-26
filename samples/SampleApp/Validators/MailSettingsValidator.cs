using FluentValidation;
using SampleApp.Settings;

namespace SampleApp.Validators;

public class MailSettingsValidator : AbstractValidator<MailSettings>
{
    public MailSettingsValidator()
    {
        RuleFor(x => x.Host).NotEmpty().MaximumLength(255);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535);
    }
}
