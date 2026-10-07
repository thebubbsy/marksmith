/**
 * Everything Web Suite - Login Logic
 */

document.addEventListener('DOMContentLoaded', async () => {
  const loginForm = document.getElementById('loginForm');
  const usernameInput = document.getElementById('username');
  const passwordInput = document.getElementById('password');
  const rememberMeCheckbox = document.getElementById('rememberMe');
  const togglePasswordBtn = document.getElementById('togglePasswordBtn');
  const fillDefaultBtn = document.getElementById('fillDefaultBtn');
  const submitBtn = document.getElementById('submitBtn');
  const btnText = document.getElementById('btnText');
  const loginAlert = document.getElementById('loginAlert');
  const engineStatusText = document.getElementById('engineStatusText');

  // Check Backend Status
  try {
    const res = await fetch('/api/auth/status');
    const data = await res.json();
    if (data.authenticated) {
      window.location.href = '/';
      return;
    }

    if (data.everythingConnected) {
      engineStatusText.innerHTML = `<span style="color: #34d399; font-weight: 600;">● Online (Port ${data.everythingPort})</span>`;
    } else {
      engineStatusText.innerHTML = `<span style="color: #fb7185; font-weight: 600;">● Disconnected (Port ${data.everythingPort})</span>`;
    }
  } catch (err) {
    engineStatusText.innerHTML = `<span style="color: #fb7185;">● Error reaching server</span>`;
  }

  // Toggle Password Visibility
  togglePasswordBtn.addEventListener('click', () => {
    const isPassword = passwordInput.getAttribute('type') === 'password';
    passwordInput.setAttribute('type', isPassword ? 'text' : 'password');
    togglePasswordBtn.textContent = isPassword ? '🙈' : '👁️';
  });

  // Auto-fill convenience button
  fillDefaultBtn.addEventListener('click', (e) => {
    e.preventDefault();
    usernameInput.value = 'user';
    passwordInput.value = 'IntelLetni4789$';
    loginAlert.style.display = 'none';
  });

  // Handle Login Submission
  loginForm.addEventListener('submit', async (e) => {
    e.preventDefault();
    loginAlert.style.display = 'none';

    const username = usernameInput.value.trim();
    const password = passwordInput.value;
    const rememberMe = rememberMeCheckbox.checked;

    if (!username || !password) {
      loginAlert.textContent = 'Please enter both username and password.';
      loginAlert.style.display = 'block';
      return;
    }

    // Loading State
    submitBtn.disabled = true;
    btnText.textContent = 'Authenticating...';

    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password, rememberMe }),
      });

      const data = await res.json();

      if (res.ok && data.success) {
        btnText.textContent = 'Success! Redirecting...';
        setTimeout(() => {
          window.location.href = '/';
        }, 300);
      } else {
        loginAlert.textContent = data.message || 'Invalid username or password.';
        loginAlert.style.display = 'block';
        submitBtn.disabled = false;
        btnText.textContent = 'Sign In';
      }
    } catch (err) {
      loginAlert.textContent = 'Network error connecting to login server.';
      loginAlert.style.display = 'block';
      submitBtn.disabled = false;
      btnText.textContent = 'Sign In';
    }
  });
});
