namespace WebHtv.Core.Configuration;

public interface IConfigurationStore
{
    Task<ConfigurationDocument> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ConfigurationDocument document, CancellationToken cancellationToken = default);
}
