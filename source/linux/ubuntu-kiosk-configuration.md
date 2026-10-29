# Ubuntu Frame Kiosk Setup

- [Install Display Stack](#install-display-stack)
- [Install Slideshow Service](#install-slideshow-service)
- [Configure Optional SMB Automount](#configure-optional-smb-automount)

## Install Display Stack

Update Ubuntu packages and install Ubuntu Frame with WPE WebKit Mir Kiosk:

```bash
sudo apt update
sudo apt upgrade -y
sudo snap install ubuntu-frame
sudo snap install wpe-webkit-mir-kiosk
sudo snap set ubuntu-frame daemon=true
sudo snap connect wpe-webkit-mir-kiosk:wayland
sudo snap set wpe-webkit-mir-kiosk daemon=true
sudo snap set wpe-webkit-mir-kiosk url=http://localhost:5500
sudo reboot
```

## Install Slideshow Service

Download the Linux release archive from `https://github.com/walljm/slideshow/releases` and unpack it into `/opt/walljm/slideshow`:

```bash
sudo mkdir -p /opt/walljm/slideshow
cd /opt/walljm/slideshow
sudo curl -LO <release_download_url>
sudo tar -xvf ./slideshow*.tar.gz
sudo cp ./linux/slideshow.service /etc/systemd/system/
sudo cp ./linux/kiosk.service /etc/systemd/system/
```

Edit `/opt/walljm/slideshow/config.json` and set `folderPath` to your local media directory (or `/opt/walljm/slideshow/imgs` when using the SMB automount below). `slideshow.service` sets `CacheDirectory=slideshow`, so the service stores its local media cache under `/var/cache/slideshow`.

Enable and start both services:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now slideshow.service
sudo systemctl enable --now kiosk.service
```

## Configure Optional SMB Automount

To pull slides from an SMB share instead of a local directory:

1. Edit `/opt/walljm/slideshow/linux/auto.smb` and replace `<ip_of_server>/path/to/photo/folder` with your SMB server address and share path.
2. Copy the credentials template to `/etc/slideshow-smb-credentials`, restrict its permissions to `root`, and fill in the SMB username and password:

```bash
sudo cp /opt/walljm/slideshow/linux/credential.txt /etc/slideshow-smb-credentials
sudo chown root:root /etc/slideshow-smb-credentials
sudo chmod 600 /etc/slideshow-smb-credentials
```

3. Set `"folderPath": "/opt/walljm/slideshow/imgs"` in `/opt/walljm/slideshow/config.json`.
4. Install `autofs` and `cifs-utils`, register the map, and reboot:

```bash
sudo apt install -y autofs cifs-utils
sudo cp /opt/walljm/slideshow/linux/auto.smb /etc/auto.slideshow-smb
sudo chmod 644 /etc/auto.slideshow-smb
printf '/- /etc/auto.slideshow-smb --timeout 60\n' | sudo tee /etc/auto.master.d/slideshow.autofs >/dev/null
sudo systemctl reboot
```
