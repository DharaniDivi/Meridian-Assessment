using Assessment.Core;
using Assessment.Core.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AssessmentOptions>(
    builder.Configuration.GetSection(AssessmentOptions.SectionName));

// Allow env vars: ASSESSMENT__BASEURL, ASSESSMENT__APIKEY
builder.Services.AddAssessmentCore();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AngularDev", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AngularDev");
app.MapControllers();

app.Run();
