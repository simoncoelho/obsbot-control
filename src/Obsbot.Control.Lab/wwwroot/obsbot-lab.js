window.obsbotLab = {
  getClickPoint: (shell, clientX, clientY) => {
    if (!shell) {
      return { X: 0.5, Y: 0.5, InsideImage: false, Reason: "Video shell was not available." };
    }

    const rect = shell.getBoundingClientRect();
    const image = shell.querySelector("img.host-stream");
    const naturalWidth = image?.naturalWidth || 16;
    const naturalHeight = image?.naturalHeight || 9;
    const shellRatio = rect.width / rect.height;
    const imageRatio = naturalWidth / naturalHeight;

    let left = rect.left;
    let top = rect.top;
    let width = rect.width;
    let height = rect.height;

    if (imageRatio > shellRatio) {
      height = rect.width / imageRatio;
      top = rect.top + ((rect.height - height) / 2);
    } else {
      width = rect.height * imageRatio;
      left = rect.left + ((rect.width - width) / 2);
    }

    const rawX = (clientX - left) / width;
    const rawY = (clientY - top) / height;
    const insideImage = rawX >= 0 && rawX <= 1 && rawY >= 0 && rawY <= 1;
    const clamp = (value) => Math.max(0, Math.min(1, value));

    return {
      X: clamp(rawX),
      Y: clamp(rawY),
      InsideImage: insideImage,
      Reason: insideImage ? null : "Click landed in the letterboxed area outside the video frame."
    };
  }
};
