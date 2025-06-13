﻿using Strike.Client.Converters;

namespace Strike.Client;

/// <summary>
/// Provides methods for sending request to and receiving data from Strike API
/// </summary>
public sealed partial class StrikeClient
{
	public static readonly Uri LiveServerUrl = new("https://api.strike.me/");
	public static readonly Uri DevelopmentServerUrl = new("https://api.dev.strike.me/");

	/// <summary>
	/// Initializes a new instance of the <see cref="StrikeClient"/> class using parameters that can all come from dependency injection
	/// </summary>
	/// <param name="options"><see cref="StrikeOptions"/> initialized from an IConfiguration section</param>
	/// <param name="httpClientFactory">A factory instance used to create <see cref="HttpClient" /> instances. If one is not provided, a service collection will be created and used instead. For more information, see <see href="https://docs.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests"/> for more information.</param>
	/// <param name="logger">A logging instance. Log entries will be provided at Information level at completion of each api call; and at Trace level with request and content details at the start and end of each api call. If not provided, a <see cref="NullLogger" /> instance will be used.</param>
	public StrikeClient(
		IOptions<StrikeOptions> options,
		IHttpClientFactory? httpClientFactory = null,
		ILogger<StrikeClient>? logger = null)
		: this(
			options.Value.Environment,
			options.Value.ApiKey,
			httpClientFactory: httpClientFactory,
			logger: logger,
			serverUrl: options.Value.ServerUrl)
	{
	}

	/// <summary>
	/// Initializes a new instance of the <see cref="StrikeClient"/> class
	/// </summary>
	/// <param name="environment">The environment</param>
	/// <param name="apiKey">The API key</param>
	/// <param name="httpClientFactory">A factory instance used to create <see cref="HttpClient" /> instances. If one is not provided, a service collection will be created and used instead. For more information, see <see href="https://docs.microsoft.com/en-us/dotnet/architecture/microservices/implement-resilient-applications/use-httpclientfactory-to-implement-resilient-http-requests"/> for more information.</param>
	/// <param name="logger">A logging instance. Log entries will be provided at Information level at completion of each api call; and at Trace level with request and content details at the start and end of each api call. If not provided, a <see cref="NullLogger" /> instance will be used.</param>
	/// <param name="serverUrl">Optional custom server url if you don't want to use predefined one based on environment</param>
	/// <remarks>
	/// Usage patterns: 
	/// A single <see cref="StrikeClient"/> may be used for all API calls, or a separate one may be used for each
	/// If the <paramref name="apiKey"/> is provided, it will be used on every call
	/// </remarks>
	public StrikeClient(
		StrikeEnvironment environment,
		string? apiKey = null,
		IHttpClientFactory? httpClientFactory = null,
		ILogger<StrikeClient>? logger = null,
		Uri? serverUrl = null)
	{
		if (serverUrl != null)
			ServerUrl = serverUrl;
		else
			Environment = environment;
		ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;

		if (httpClientFactory == null)
		{
			var collection = new ServiceCollection();
			_ = collection.AddStrikeHttpClient();
			var serviceProvider = collection.BuildServiceProvider();
			_clientFactory = serviceProvider.GetService<IHttpClientFactory>()!;
		}
		else
			_clientFactory = httpClientFactory;

		_logger = logger ?? new NullLogger<StrikeClient>();
	}

	private readonly IHttpClientFactory _clientFactory;
	private readonly ILogger _logger;
	private Uri _serverUrl = LiveServerUrl;
	private StrikeEnvironment _environment = StrikeEnvironment.Live;

	internal static readonly JsonSerializerOptions JsonSerializerOptions =
		new JsonSerializerOptions
		{
#if DEBUG
			WriteIndented = true,
#else
			WriteIndented = false,
#endif
			DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
			PropertyNameCaseInsensitive = true,
		}
			.AddStrikeConverters();

	/// <summary>
	/// Set true if any unsuccessful response should throw StrikeApiException.
	/// Default: false
	/// </summary>
	public bool ThrowOnError { get; set; }

	/// <summary>
	/// Target Strike environment
	/// </summary>
	public StrikeEnvironment Environment
	{
		get => _environment;
		set
		{
			_serverUrl = value switch
			{
				StrikeEnvironment.Live => LiveServerUrl,
				StrikeEnvironment.Development => DevelopmentServerUrl,
				_ => throw new InvalidOperationException("Please configure ServerUrl instead of custom environment")
			};
			_environment = value;
		}
	}

	/// <summary>
	/// Target Strike server URL, it is automatically derived from Environment unless you set it manually
	/// </summary>
	public Uri ServerUrl
	{
		get => _serverUrl;
		set
		{
			_serverUrl = value;
			_environment = StrikeEnvironment.Custom;
		}
	}

	/// <summary>
	/// The access token used for all API calls.
	/// </summary>
	public string? ApiKey { get; set; }

	/// <summary>
	/// Debug option to include the raw json in the returned DTO
	/// </summary>
	public bool ShowRawJson { get; set; }

	/// <summary>
	/// Additional request headers used for all API calls.
	/// </summary>
	public Dictionary<string, string>? AdditionalHeaders { get; } = [];

	private ResponseParser Post<TRequest>(string path, TRequest? request) where TRequest : RequestBase
	{
		return Send(path, HttpMethod.Post, request);
	}

	private ResponseParser Patch(string path)
	{
		return Send<RequestBase>(path, HttpMethod.Patch, null);
	}

	private ResponseParser Delete(string path)
	{
		return Send<RequestBase>(path, HttpMethod.Delete, null);
	}

	private ResponseParser Patch<TRequest>(string path, TRequest? request) where TRequest : RequestBase
	{
		return Send(path, HttpMethod.Patch, request);
	}

	private ResponseParser Get(string path)
	{
		return Send<RequestBase>(path, HttpMethod.Get, request: null);
	}

	private ResponseParser Send<TRequest>(string path, HttpMethod method, TRequest? request)
		where TRequest : RequestBase
	{
		var client = _clientFactory.CreateClient(StrikeOptions.HttpClientName);
		var baseUrl = _serverUrl;
		var url = new Uri(baseUrl, path);
		_logger.LogTrace("Initiating request. Method: {Method}; Url: {Url}; Content: {@Content}",
			method.Method.ToUpperInvariant(), url, request);

#pragma warning disable CA2000 // Dispose objects before losing scope
		var requestMessage = new HttpRequestMessage
		{
			Method = method,
			RequestUri = url,
			Content = request == null ? null : JsonContent.Create(request, options: JsonSerializerOptions)
		};
#pragma warning restore CA2000 // Dispose objects before losing scope

		AddAuthenticationHeader(requestMessage);
		AddIdempotencyHeader(request as IdempotentRequestBase, requestMessage);
		AddRequestHeaders(requestMessage, AdditionalHeaders);

		if (request != null)
			AddRequestHeaders(requestMessage, request.AdditionalHeaders);

		return new ResponseParser
		{
			Request = requestMessage,
			Message = client.SendAsync(requestMessage),
			Url = url.ToString(),
			IncludeRawJson = request?.ShowRawJson ?? ShowRawJson,
			Logger = _logger,
			ThrowOnError = ThrowOnError
		};
	}

	private void AddAuthenticationHeader(HttpRequestMessage requestMessage)
	{
		requestMessage.Headers.Add("Authorization", $"Bearer {ApiKey}");
	}

	private static void AddIdempotencyHeader<TRequest>(TRequest? request, HttpRequestMessage requestMessage) where TRequest : IdempotentRequestBase
	{
		if (request?.IdempotencyKey == null)
			return;

		requestMessage.Headers.Add("idempotency-key", request.IdempotencyKey.ToString());
	}

	private static void AddRequestHeaders(HttpRequestMessage requestMessage, Dictionary<string, string>? headers)
	{
		if (headers != null)
		{
			foreach (var header in headers)
			{
				requestMessage.Headers.Add(header.Key, header.Value);
			}
		}
	}

	private static string ConstructUrlParams(params (string Key, object? Value)[] parameters)
	{
		var nonEmptyParams = parameters.Where(p => p.Value != null);
		var urlPart = string.Join("&", nonEmptyParams.Select(p => $"${p.Key}={ConstructUrlValue(p.Value)}"));
		return urlPart.Length > 0 ? $"?{urlPart}" : string.Empty;
	}

	private static string ConstructUrlValue(object? value)
	{
		if (value == null)
			return string.Empty;
		if (value is string str)
			return str;
		if (value is IEnumerable<object?> items)
		{
			return string.Join(",", items.Where(x => x != null));
		}
		if (value is IEnumerable<Guid> guids)
		{
			return string.Join(",", guids);
		}

		return value.ToString() ?? string.Empty;
	}

	private readonly struct ResponseParser
	{
		public HttpRequestMessage Request { get; init; }
		public Task<HttpResponseMessage> Message { get; init; }
		public string Url { get; init; }
		public bool IncludeRawJson { get; init; }
		public bool ThrowOnError { get; init; }
		public ILogger Logger { get; init; }

		public async Task<TResponse> ParseResponse<TResponse>() where TResponse : ResponseBase, new()
		{
			try
			{
				using var response = await Message.ConfigureAwait(false);

				Logger.LogInformation("Completed request. Url: {Url}, Status Code: {StatusCode}.", Url,
					response.StatusCode);

				var result = await BuildResponse<TResponse>(response).ConfigureAwait(false);
				Logger.LogTrace("Completed request details. Url: {Url}; Response: {@Result}",
					Url,
					result);
				return result;
			}
			catch (Exception ex)
			{
				if (ThrowOnError)
					throw;

				var status = HttpStatusCode.ServiceUnavailable;

#if NET6_0_OR_GREATER
				var httpException = ex as HttpRequestException;
				status = httpException?.StatusCode ?? HttpStatusCode.ServiceUnavailable;
#endif

				var errorResponse = new TResponse
				{
					Error = new StrikeError
					{
						Data = new StrikeApiError
						{
							Status = (int)status,
							Code = "API_UNAVAILABLE",
							Message = ex.Message
						}
					},
					StatusCode = status
				};
				return errorResponse;
			}
			finally
			{
				Request.Dispose();
			}
		}

		private async Task<TResponse> BuildResponse<TResponse>(HttpResponseMessage response)
			where TResponse : ResponseBase, new()
		{
			if (response.IsSuccessStatusCode)
			{
				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
				// some APIs like PATCH /v1/currency-exchange-quotes/ return empty response
				if (string.IsNullOrWhiteSpace(json))
					json = "{}";

				var result = JsonSerializer.Deserialize<TResponse>(json, options: JsonSerializerOptions);
				result!.StatusCode = response.StatusCode;

				if (IncludeRawJson)
					result.RawJson = json;

				return result;

			}
			else
			{
				var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

				var statusCode = (int)response.StatusCode;
				var error = ParseError(statusCode, json);

				if (ThrowOnError)
					throw new StrikeApiException(
						$"API error, status: {statusCode}, error: {error.Data.Code} {error.Data.Message}");

				var result = new TResponse { Error = error, StatusCode = response.StatusCode };

				if (IncludeRawJson)
					result.RawJson = json;
				return result;
			}
		}

		private static StrikeError ParseError(int statusCode, string json)
		{
			try
			{
				return JsonSerializer.Deserialize<StrikeError>(json, options: JsonSerializerOptions)!;
			}
			catch (JsonException ex)
			{
				return new StrikeError
				{
					Data = new StrikeApiError { Status = statusCode, Code = "API_UNAVAILABLE", Message = ex.Message }
				};
			}
		}
	}
}
