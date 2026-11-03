FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish HoaSap.API/HoaSap.API.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN chmod 0644 /app/appsettings*.json \
    && install -d -o "$APP_UID" -g "$APP_UID" -m 0750 /app/App_Data/media \
    && chown -R "$APP_UID:$APP_UID" /app
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HoaSap.API.dll"]
