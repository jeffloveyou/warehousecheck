# M0-11 容器映像：直接使用 scripts/publish.ps1 的 framework-dependent 輸出（linux-x64 或 linux-arm64），
# 不在容器內編譯，確保與原生部署是同一份發布檔。
# 建置（在 repo 根目錄）：
#   docker build -f deploy/docker/Dockerfile --build-arg RELEASE_DIR=artifacts/publish/<版本資料夾> -t warehouse:<版本> .
# 執行：
#   docker run -d --name warehouse-c --network host -e ASPNETCORE_URLS=http://localhost:5082 \
#     -v /etc/warehouse:/config:ro warehouse:<版本>
FROM mcr.microsoft.com/dotnet/aspnet:10.0

ARG RELEASE_DIR
WORKDIR /app
COPY ${RELEASE_DIR}/ /app/

# 設定一律來自掛載的 /config（與映像分離）；連線字串不放進映像
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    WAREHOUSE_CONFIG_DIR=/config

# 官方映像內建的非 root 使用者
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Warehouse.Web.dll"]
