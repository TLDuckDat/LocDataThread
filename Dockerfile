# Image cho Railway (hoặc bất kỳ nơi chạy Docker nào). Chỉ build app web; test chạy riêng.
# Bí mật không nằm trong image: đặt biến môi trường ConnectionStrings__Default khi chạy.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ThreadsToxic.Web/ThreadsToxic.Web.csproj src/ThreadsToxic.Web/
RUN dotnet restore src/ThreadsToxic.Web/ThreadsToxic.Web.csproj
COPY src/ThreadsToxic.Web/ src/ThreadsToxic.Web/
RUN dotnet publish src/ThreadsToxic.Web/ThreadsToxic.Web.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# App đọc cổng từ PORT (Railway tự đặt); 8080 khi chạy docker run thủ công
ENV ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "ThreadsToxic.Web.dll"]
