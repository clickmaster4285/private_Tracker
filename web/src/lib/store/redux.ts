import { createSlice, configureStore, createAsyncThunk, PayloadAction } from '@reduxjs/toolkit';
import { authApi, type AuthUser } from '@/lib/api';

// ──────────────────────────
// Auth Slice
// ──────────────────────────

interface AuthState {
  user: AuthUser | null;
  isLoading: boolean;
  isAuthenticated: boolean;
  error: string | null;
}

const initialState: AuthState = {
  user: null,
  isLoading: true, // Start loading until we check auth status
  isAuthenticated: false,
  error: null,
};

// Async thunk: check auth status from server cookie.
// /auth/check answers 200 {authenticated:false} for an EXPIRED access token
// (optional-auth middleware), so before giving up we try to revive the session
// via the rotating refresh-token cookie and re-check once.
export const checkAuth = createAsyncThunk('auth/check', async (_, { rejectWithValue }) => {
  try {
    let response = await authApi.check();
    if (!response.authenticated) {
      const revived = await authApi.refresh();
      if (revived) {
        response = await authApi.check();
      }
    }
    return response;
  } catch (err: unknown) {
    if (err instanceof Error) {
      return rejectWithValue(err.message);
    }
    return rejectWithValue('Failed to check authentication');
  }
});

// Async thunk: login
export const loginUser = createAsyncThunk(
  'auth/login',
  async ({ email, password }: { email: string; password: string }, { rejectWithValue }) => {
    try {
      const response = await authApi.login(email, password);
      return response;
    } catch (err: unknown) {
      if (err instanceof Error) {
        return rejectWithValue(err.message);
      }
      return rejectWithValue('Login failed');
    }
  },
);

// Async thunk: logout
export const logoutUser = createAsyncThunk('auth/logout', async (_, { rejectWithValue }) => {
  try {
    await authApi.logout();
  } catch (err: unknown) {
    if (err instanceof Error) {
      return rejectWithValue(err.message);
    }
    return rejectWithValue('Logout failed');
  }
});

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    clearError(state) {
      state.error = null;
    },
    setUser(state, action: PayloadAction<AuthUser | null>) {
      state.user = action.payload;
      state.isAuthenticated = !!action.payload;
    },
  },
  extraReducers: (builder) => {
    // Check auth
    builder.addCase(checkAuth.pending, (state) => {
      state.isLoading = true;
      state.error = null;
    });
    builder.addCase(checkAuth.fulfilled, (state, action) => {
      state.isLoading = false;
      state.isAuthenticated = action.payload.authenticated;
      state.user = action.payload.user || null;
    });
    builder.addCase(checkAuth.rejected, (state) => {
      state.isLoading = false;
      state.isAuthenticated = false;
      state.user = null;
    });

    // Login
    builder.addCase(loginUser.pending, (state) => {
      state.isLoading = true;
      state.error = null;
    });
    builder.addCase(loginUser.fulfilled, (state, action) => {
      state.isLoading = false;
      state.isAuthenticated = true;
      state.user = action.payload.user;
      state.error = null;
    });
    builder.addCase(loginUser.rejected, (state, action) => {
      state.isLoading = false;
      state.isAuthenticated = false;
      state.user = null;
      state.error = action.payload as string || 'Login failed';
    });

    // Logout
    builder.addCase(logoutUser.fulfilled, (state) => {
      state.user = null;
      state.isAuthenticated = false;
      state.isLoading = false;
      state.error = null;
    });
    builder.addCase(logoutUser.rejected, (state) => {
      // Even if logout API fails, clear local state
      state.user = null;
      state.isAuthenticated = false;
      state.isLoading = false;
    });
  },
});

export const { clearError, setUser } = authSlice.actions;

// ──────────────────────────
// Store
// ──────────────────────────

export const store = configureStore({
  reducer: {
    auth: authSlice.reducer,
  },
});

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
