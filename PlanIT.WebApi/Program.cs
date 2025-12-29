// todos los using necesarios
using Microsoft.EntityFrameworkCore;
using PlanIT.BusinessLogic.Interfaces;
using PlanIT.BusinessLogic.Services;
using PlanIT.DataAccess.Interfaces;
using PlanIT.Domain;
using PlanIT.Domain.Interfaces;
using PlanIT.Infrastructure.Data;
using PlanIT.BusinessLogic.DTOs;
using PlanIT.Infrastructure.Repositories; // Aca está la clase de UserRepository, casi me mareo
// Importaciones para Token
using PlanIT.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;
//NUEVOS DE IA 
using PlanIT.Infrastructure.Integrations;
using Microsoft.Extensions.AI;
using OpenAI;

// ========================================================================================================================
// 0. Builder, aca inicia la aplicacion 
// ==========================================================================================================================
var builder = WebApplication.CreateBuilder(args);



// ===========================================================================================================================
// 1. FrameWork & Mechanisms Setup (Capa Externa)
// ===========================================================================================================================

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(setup =>
{
    // Configuración para que aparezca el botón "Authorize" con JWT
    setup.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. \r\n\r\n Enter 'Bearer' [space] and then your token in the text input below.\r\n\r\nExample: \"Bearer 12345abcdef\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    setup.AddSecurityRequirement(new OpenApiSecurityRequirement()
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                },
                Scheme = "oauth2",
                Name = "Bearer",
                In = ParameterLocation.Header,
            },
            new List<string>()
        }
    });
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
    });
});

// ===========================================================================================================================
// 2. Infraestruture Setup (Capa de Infraestructura)
// ===========================================================================================================================

var connectionString = builder.Configuration.GetConnectionString("PlanITDbConnection")
    ?? throw new InvalidOperationException("Connection string not found.");

// CAMBIO ACA: UseNpgsql
builder.Services.AddDbContext<PlanITDbContext>(options =>
    options.UseNpgsql(connectionString));

// ===========================================================================================================================
// 3. Registros de DEPENDENCIAS (Inyeccion de Control)
// Contrato entre capas: Definición de Contratos (Domain) -----> Implementación (Infrastructure / BusinessLogic). Domain define las interfaces (IUserRepository, IUnitOfWork).
// ===========================================================================================================================

builder.Services.AddScoped<ITravelRepository, TravelRepository>();
builder.Services.AddScoped<ITravelService, TravelService>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddScoped<IJwtProvider, JwtProvider>();

// Configuracion de autenticacion
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)
            )
        };
    });

builder.Services.AddAuthorization(); // Necesario para .RequireAuthorization()

// ===========================================================================================================================
// INTEGRACIONES REALES (Google & OpenAI) 
// ===========================================================================================================================

var keyParaOpenAi = builder.Configuration["ApiKeys:OpenAI"]
    ?? throw new InvalidOperationException("OpenAI Key no encontrada.");

// 1. Instanciamos el cliente general
OpenAI.OpenAIClient openAiClient = new(keyParaOpenAi);

// 2. Obtenemos el cliente de Chat
var openAiChatClient = openAiClient.GetChatClient("gpt-4o-mini");

// 3. Conversión (CORREGIDO: Es .AsChatClient, sin la 'I')
// Al usar la versión 2.1.0-beta.2, este método aparece mágicamente.
IChatClient chatClient = Microsoft.Extensions.AI.OpenAIClientExtensions.AsIChatClient(openAiChatClient);

builder.Services.AddChatClient(chatClient);
builder.Services.AddScoped<IIaAssistantService, OpenAiAssistantService>();

// Mantenemos el FakeEmailService por ahora (LUCHO ESTO TENES QUE HACER VOS)
builder.Services.AddScoped<IEmailService, FakeEmailService>();

builder.Services.AddScoped<IApiIntegrationService, FakeApiIntegrationService>();

builder.Services.AddScoped<ITravelService, TravelService>();

//Google 
builder.Services.AddScoped<IApiIntegrationService, GooglePlacesService>();




// ===========================================================================================================================
// Para construir la app:

var app = builder.Build();



// ===========================================================================================================================
// 4. MIDDLEWARE
// ===========================================================================================================================
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

// Orden de token
app.UseAuthentication();
app.UseAuthorization();


// Habilitar archivos esáticos y manejos de rutas SPA
app.UseDefaultFiles(); 
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
// ===========================================================================================================================
// 5. ENDPOINTS (Presentacion)
// ===========================================================================================================================

// ENDPOINT POST: CREAR Viaje
app.MapPost("/api/travels", async (
    TravelCreationDto dto,
    ITravelService travelService) =>
{
    try
    {
        // 1. Llamar al servicio
        var createdTravel = await travelService.CreateTravelAsync(dto);

        // 2. Devolver 201 Created con el objeto
        return Results.Created($"/api/travels/{createdTravel.Id}", createdTravel);
    }
    catch (ArgumentException ex)
    {
        // 3. Manejar errores de validación (400)
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (Exception ex)
    {
        // 4. Manejar errores del servidor (500)
        return Results.Problem("Ocurrió un error inesperado: " + ex.Message);
    }
})
.WithName("CreateTravel")
.RequireAuthorization();

// ENDPOINT GET: LISTAR Viajes por Usuario
app.MapGet("/api/travels/user/{userId:guid}", async (
    Guid userId,
    ITravelService travelService) =>
{
    var travels = await travelService.GetTravelsByUserIdAsync(userId);

    return travels == null || !travels.Any()
        ? Results.NotFound(new { message = $"No se encontraron viajes para el usuario {userId}." })
        : Results.Ok(travels);
})
.WithName("GetUserTravels")
.RequireAuthorization();

// Reset de Password - nuevos endpoints
app.MapPost("/api/auth/forgot-password", async (
    ForgotPasswordDto dto,
    IUserService userService) =>
{
    await userService.RequestPasswordResetAsync(dto);
    return Results.Ok(new { message = "Si el email está registrado, se ha enviado una instrucción para resetear la contraseña." });
});

// Ejecución del reset de password
app.MapPost("/api/auth/reset-password", async (
    ResetPasswordDto dto,
    IUserService userService) =>
{
    try
    {
        await userService.ResetPasswordAsync(dto);
        return Results.Ok(new { message = "Contraseña reseteada exitosamente." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem("Ocurrió un error inesperado: " + ex.Message);
    }
});

// ENDPOINT POST: Generar Itinerario con IA 
app.MapPost("/api/travels/{id:guid}/generate", async (
    Guid id,
    ITravelRepository travelRepo,
    IApiIntegrationService googleService, //  INYECTO GOOGLE ACA
    IChatClient chatClient,
    IUnitOfWork unitOfWork) =>   //aca inyecte el Unit of Work
{
    try
    {
        // 1. Buscar el viaje
        var travel = await travelRepo.GetByIdAsync(id);
        if (travel == null) return Results.NotFound("Viaje no encontrado");

        // 2. Buscar lugares reales en Google 
        // Esto busca "Anime en Japon" o "Parrilla en Corrientes"
        var places = await googleService.GetNearbyPointsOfInterestAsync(travel.Destination, travel.TravelStyle);

        // 3. Se prepara la info para el prompt
        // Se pasa el nombre Y las cordenadas para que se incluyan en las respeuestas 
        var googleContext = string.Join("\n", places.Select(p =>
            $"- {p.Name} (Lat: {p.Latitude}, Lng: {p.Longitude}, Rating: {p.Rating}⭐)"));

        // 4. Crear el Prompt recargado (Parte Importate)  
        var prompt = $@"
            Eres un arquitecto de viajes experto. Tu trabajo es generar un itinerario estructurado.
            
            DATOS DEL VIAJE:
            - Destino: {travel.Destination}
            - Duración: {travel.DurationDays} días
            - Presupuesto: {travel.EstimatedBudget} USD
            - Estilo: {travel.TravelStyle}

            LUGARES DISPONIBLES (ÚSALOS):
            {googleContext}

            INSTRUCCIONES:
            1. Crea un itinerario detallado día por día.
            2. Si usas uno de los 'LUGARES DISPONIBLES', copia sus coordenadas exactas.
            3. Si inventas una actividad (ej: 'Caminar por el centro'), deja coordenadas en 0 o estima.
            4. IMPORTANTE: Tu respuesta debe ser SOLO un JSON válido con esta estructura exacta:

            {{
              ""trip_title"": ""Título creativo del viaje"",
              ""days"": [
                {{
                  ""day_number"": 1,
                  ""theme"": ""Tema del día (ej: Historia romana)"",
                  ""activities"": [
                    {{
                      ""time"": ""09:00"",
                      ""place_name"": ""Nombre del lugar"",
                      ""description"": ""Breve descripción de qué hacer"",
                      ""category"": ""Food|Culture|Nature|Shopping"",
                      ""coordinates"": {{ ""lat"": 0.0, ""lng"": 0.0 }},
                      ""price_estimate"": ""$20"",
                      ""requires_ticket"": true/false
                    }}
                  ]
                }}
              ]
            }}
            
            No incluyas texto antes ni después del JSON. Solo el JSON.";

        // 5. Preguntar a la IA de OPEN
        var mensajes = new List<ChatMessage> { new(ChatRole.User, prompt) };
        var respuesta = await chatClient.GetResponseAsync(mensajes);

        // Esto para Limpiar si la IA manda algo raro
        var jsonLimpio = respuesta.ToString().Replace("```json", "").Replace("```", "").Trim();

        // 6. Guardar y Devolver
        travel.ItineraryJson = respuesta.ToString(); 
        travel.IsGenerated = true;

        travelRepo.Update(travel);           // entonces actualizo aca
        await unitOfWork.SaveChangesAsync(); // guardo en Postgre aca

        // Arreglamos el JSON crudo pero parseado para que el Swagger lo muestre lindo gg
        return Results.Ok(System.Text.Json.JsonDocument.Parse(jsonLimpio));
    }
    catch (Exception ex)
    {
        return Results.Problem("Error generando itinerario: " + ex.Message);
    }
})
.RequireAuthorization();


// ENDPOINT POST: Registro Usuario
app.MapPost("/api/auth/register", async (
    UserRegisterDto dto,
    IUserService userService) =>
{
    try
    {
        await userService.RegisterAsync(dto);
        return Results.Ok(new { message = "Usuario registrado exitosamente." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem("Ocurrió un error inesperado: " + ex.Message);
    }
})
.WithName("RegisterUser");


// ENDPOINT POST: Login User (Mejorado por Mati |B ) 
app.MapPost("/api/auth/login", async (
    UserLoginDto dto,
    IUserService userService,
    IUserRepository userRepo) => // 1. Inyectamos el Repo para buscar al usuario
{
    try
    {
        // A. Obtenemos el Token 
        var token = await userService.LoginAsync(dto);

        // B. Buscamos los datos del usuario usando su email
        var user = await userRepo.GetByEmailAsync(dto.Email);

        if (user == null)
            return Results.BadRequest(new { message = "Usuario no encontrado." }); // Esto es por si el login tiro algun error o algo raro

        // C. Devolvemos el Token Y el ID juntos
        return Results.Ok(new
        {
            Token = token,
            UserId = user.Id,   // ID ACA
            Email = user.Email  // Email por las dudas xd
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Problem("Ocurrió un error inesperado: " + ex.Message);
    }
})
.WithName("LoginUser");


// ENDPOINT POST: Login con Google
app.MapPost("/api/auth/google-login", async (
    GoogleLoginDto dto, 
    IUserService userService) =>
{
    try
    {
        var token = await userService.LoginWithGoogleAsync(dto.GoogleToken);
        return Results.Ok(new { token });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
})
.WithName("GoogleLogin");


// ENDPOINT EXTRA: CHAT con la IA, asi probamos cosas
app.MapPost("/api/ai/chat", async (IChatClient chatClient, string pregunta) =>
{
    var mensajes = new List<ChatMessage>
    {
        new(ChatRole.System, "Eres un asistente de viajes sarcástico y divertido."),
        new(ChatRole.User, pregunta)
    };

    var respuesta = await chatClient.GetResponseAsync(mensajes);
    return Results.Ok(new { TuPregunta = pregunta, RespuestaIA = respuesta.ToString() });
})
.RequireAuthorization();

app.Run();



// Estos records son necesarios para que los Mocks compilen en WebAPI.
// despues hay que ELIMINARLOS y usar los reales de las capas correspondientes mientras no borren esto porfavor jajaja 
public record ApiPlaceDetail(string Name, string PlaceId, double Latitude, double Longitude, float Rating);
public record WeatherForecast(DateTime Date, string Description, float MaxTemp);

// =========================================================================================================================================================
// CLASES DE MOCK (NECESARIAS PARA QUE LA DI EN PROGRAM.CS COMPILE)
// =========================================================================================================================================================
public class FakeApiIntegrationService : IApiIntegrationService
{
    public Task<IEnumerable<ApiPlaceDetail>> GetNearbyPointsOfInterestAsync(string destination, string travelStyle) => Task.FromResult(Enumerable.Empty<ApiPlaceDetail>());
    public Task<WeatherForecast> GetWeatherForecastAsync(string destination, DateTime startDate, int durationDays) => Task.FromResult(new WeatherForecast(startDate, "Fake Weather", 25f));

    Task<IEnumerable<PlanIT.DataAccess.Interfaces.ApiPlaceDetail>> IApiIntegrationService.GetNearbyPointsOfInterestAsync(string destination, string travelStyle)
    {
        throw new NotImplementedException();
    }

    Task<PlanIT.DataAccess.Interfaces.WeatherForecast> IApiIntegrationService.GetWeatherForecastAsync(string destination, DateTime startDate, int durationDays)
    {
        throw new NotImplementedException();
    }
}
public class FakeIaAssistantService : IIaAssistantService
{
    public Task<string> GenerateItineraryJsonAsync(Travel travel, IEnumerable<ApiPlaceDetail> placeDetails) => Task.FromResult("{}");
    public Task<string> ChatWithAssistantAsync(string conversationHistoryJson, string newUserMessage) => Task.FromResult("Fake response from AI.");

    public Task<string> GenerateItineraryJsonAsync(Travel travel, IEnumerable<PlanIT.DataAccess.Interfaces.ApiPlaceDetail> placeDetails)
    {
        throw new NotImplementedException();
    }
}

public class FakeEmailService : IEmailService
{
    private readonly ILogger<FakeEmailService> _logger;
    public FakeEmailService(ILogger<FakeEmailService> logger)
    {
        _logger = logger;
    }
    public async Task SendPasswordResetEmailAsync(string toEmail, string resetToken)
    {
        // Simular el envío de correo electrónico
        _logger.LogInformation($"Simulando el envío de correo a {toEmail} con el token de reseteo: {resetToken}");
        await Task.CompletedTask;
    }
}