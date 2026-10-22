# M0-11 容器映像：直接使用 scripts/publish.ps1 的 framework-dependent 輸出（linux-x64 或 linux-arm64），
# 不在容器內編譯，確保與原生部署是同一份發布檔。
# 建置（在 repo 根目錄）：
#   docker build -f deploy/docker/Dockerfile --build-arg RELEASE_DIR=artifacts/publish/<版本資料夾> -t warehouse:<版本> .
# 執行：
#   docker run -d --name warehouse-c --network host --restart unless-stopped \
#     --group-add "$(getent group warehouse | cut -d: -f3)" \
#     -e ASPNETCORE_URLS=http://localhost:5082 -v /etc/warehouse:/config:ro \
#     -v /var/lib/warehouse/data/attachments:/data/attachments warehouse:<版本>
# --group-add：/etc/warehouse 為 0750 root:warehouse，容器內的 app 使用者（UID 1654）需加入主機的
#   warehouse 群組才能讀設定；不要為此放寬設定檔權限。
# ARM64 映像：在 repo 根目錄加 --platform linux/arm64，RELEASE_DIR 指向 linux-arm64 發布資料夾。
FROM mcr.microsoft.com/dotnet/aspnet:10.0

ARG RELEASE_DIR
WORKDIR /app
COPY ${RELEASE_DIR}/ /app/

# 設定一律來自掛載的 /config（與映像分離）；連線字串不放進映像
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    WAREHOUSE_CONFIG_DIR=/config \
    Storage__AttachmentsDirectory=/data/attachments
# 附件（倉位圖片）要掛載持久化磁碟：-v <主機目錄>:/data/attachments（主機目錄需讓 UID 1654 可寫），並納入備份。
# 先建好目錄並交給 app 使用者：沒掛載時也不會因權限失敗，但資料留在容器層、重建就消失，所以宣告 VOLUME 提醒 mkdir -p /data/attachments && chown $APP_UID /data/attachments /data/attachments

# 官方映像內建的非 root 使用者
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Warehouse.Web.dll"]
