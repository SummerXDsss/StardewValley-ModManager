import { useEffect, useState } from "react";
import { Alert, App, Avatar, Button, Drawer, Empty, Input, Segmented, Space, Table, Tag } from "antd";
import type { TableProps } from "antd";
import { CloudDownloadOutlined, ExportOutlined, InfoCircleOutlined, SearchOutlined } from "@ant-design/icons";
import { PageTitle } from "../shared";
import { discoverMods, downloadNexusFile, getNexusModDetails, openRemoteUrl } from "../../api";
import type { NexusFileVersion, NexusModDetails, RemoteMod } from "../../types";

export function DownloadsPage() {
  const { message } = App.useApp();
  const [search, setSearch] = useState("");
  const [source, setSource] = useState("全部");
  const [remoteMods, setRemoteMods] = useState<RemoteMod[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string>();
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsError, setDetailsError] = useState<string>();
  const [details, setDetails] = useState<NexusModDetails>();
  const [downloadingFile, setDownloadingFile] = useState<string>();

  const mods = remoteMods.filter((mod) => {
    const matchesSource = source === "全部" || mod.source === source;
    const matchesSearch = `${mod.name} ${mod.author} ${mod.summary}`.toLowerCase().includes(search.toLowerCase());
    return matchesSource && matchesSearch;
  });

  const loadMods = async () => {
    setLoading(true);
    setLoadError(undefined);
    try {
      setRemoteMods(await discoverMods());
    } catch (error) {
      setLoadError(String(error));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    void loadMods();
  }, []);

  const openMod = async (mod: RemoteMod) => {
    const download = mod.downloadUrl;
    await openRemoteUrl(download ?? mod.pageUrl);
    message.success(download ? "已在浏览器中打开官方发布包" : "已打开官方发布页");
  };

  const showNexusDetails = async (mod: RemoteMod) => {
    if (!mod.providerId) return;
    setDetailsOpen(true);
    setDetails(undefined);
    setDetailsError(undefined);
    setDetailsLoading(true);
    try {
      setDetails(await getNexusModDetails(mod.providerId));
    } catch (error) {
      setDetailsError(String(error));
    } finally {
      setDetailsLoading(false);
    }
  };

  const downloadVersion = async (version: NexusFileVersion) => {
    if (!details) return;
    setDownloadingFile(version.id);
    try {
      const downloaded = await downloadNexusFile(details.gameScopedId, version.gameScopedId);
      message.success(`已下载到 ${downloaded.path}`);
    } catch (error) {
      message.error(String(error));
    } finally {
      setDownloadingFile(undefined);
    }
  };

  const sourceOptions = ["全部", ...Array.from(new Set(remoteMods.map((mod) => mod.source)))];

  const columns: TableProps<RemoteMod>["columns"] = [
    {
      title: "Mod",
      dataIndex: "name",
      render: (_, mod) => (
        <div className="remote-mod-row">
          <Avatar shape="square" size={48} src={mod.imageUrl}>
            {mod.name.slice(0, 1)}
          </Avatar>
          <div className="remote-mod">
            <strong>{mod.name}</strong>
            <span>{mod.summary}</span>
            <small>{mod.author}</small>
          </div>
        </div>
      ),
    },
    { title: "来源", dataIndex: "source", width: 138, render: (value) => <Tag>{value}</Tag> },
    { title: "版本", dataIndex: "version", width: 90 },
    { title: "热度", dataIndex: "popularity", width: 110, responsive: ["lg"] },
    { title: "兼容性", dataIndex: "compatibility", width: 108, render: (value) => <Tag color="green">{value}</Tag> },
    {
      title: "操作",
      key: "action",
      width: 210,
      render: (_, mod) => (
        <Space size={6}>
          {mod.source === "Nexus Mods" && mod.providerId && (
            <Button icon={<InfoCircleOutlined />} onClick={() => void showNexusDetails(mod)}>详情</Button>
          )}
          <Button
            type={mod.downloadUrl ? "primary" : "default"}
            icon={mod.downloadUrl ? <CloudDownloadOutlined /> : <ExportOutlined />}
            onClick={() => void openMod(mod)}
          >
            {mod.downloadUrl ? "下载" : "发布页"}
          </Button>
        </Space>
      ),
    },
  ];

  return (
    <section>
      <PageTitle title="发现 Mod" subtitle="直接浏览来自可信上游的星露谷物语 Mod" />
      <div className="download-browser">
        <Input
          size="large"
          prefix={<SearchOutlined />}
          placeholder="搜索 Mod、作者或功能"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          allowClear
        />
        <Button size="large">安装本地压缩包</Button>
      </div>
      <div className="source-filter">
        <span>来源</span>
        <Segmented options={sourceOptions} value={source} onChange={(value) => setSource(String(value))} />
        <small>共 {mods.length} 个结果</small>
      </div>
      {loadError && (
        <Alert
          type="warning"
          showIcon
          message="上游数据暂时不可用"
          description={loadError}
          action={<Button onClick={() => void loadMods()}>重试</Button>}
        />
      )}
      <div className="table-shell remote-table">
        <Table
          rowKey="id"
          columns={columns}
          dataSource={mods}
          loading={loading}
          pagination={{ pageSize: 6, showSizeChanger: false }}
          scroll={{ x: 760 }}
          locale={{ emptyText: <Empty description="没有匹配的 Mod" image={Empty.PRESENTED_IMAGE_SIMPLE} /> }}
        />
      </div>
      <Drawer
        title={details?.name ?? "Nexus Mod 详情"}
        open={detailsOpen}
        width={680}
        onClose={() => setDetailsOpen(false)}
      >
        {detailsLoading && <div className="drawer-loading">正在读取 Nexus 文件与版本...</div>}
        {detailsError && (
          <Alert
            type="warning"
            showIcon
            message="无法读取完整详情"
            description={`${detailsError}。请在设置中填写有效的 Nexus API Key。`}
          />
        )}
        {details && (
          <div className="nexus-files">
            <div className="nexus-detail-summary">
              <span>Mod ID {details.gameScopedId}</span>
              <Button icon={<ExportOutlined />} onClick={() => void openRemoteUrl(details.pageUrl)}>打开发布页</Button>
            </div>
            {details.files.length === 0 && <Empty description="这个 Mod 暂无文件" image={Empty.PRESENTED_IMAGE_SIMPLE} />}
            {details.files.map((file) => (
              <section className="nexus-file" key={file.id}>
                <header>
                  <div><strong>{file.name}</strong><span>{file.versionsCount} 个版本</span></div>
                  <Tag color={file.isActive ? "green" : "default"}>{file.isActive ? "可用" : "已归档"}</Tag>
                </header>
                <div className="nexus-versions">
                  {file.versions.map((version) => (
                    <div className="nexus-version" key={version.id}>
                      <div>
                        <strong>{version.version}</strong>
                        <span>{version.name}</span>
                        <small>{version.category} · {version.uploadedAt.slice(0, 10)}</small>
                      </div>
                      {version.isPrimary && <Tag color="gold">主文件</Tag>}
                      <Button
                        type="primary"
                        icon={<CloudDownloadOutlined />}
                        loading={downloadingFile === version.id}
                        onClick={() => void downloadVersion(version)}
                      >
                        下载
                      </Button>
                    </div>
                  ))}
                </div>
              </section>
            ))}
          </div>
        )}
      </Drawer>
    </section>
  );
}
