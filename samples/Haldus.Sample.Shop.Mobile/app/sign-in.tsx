import { useMutation } from '@tanstack/react-query';
import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { KeyboardAvoidingView, Platform, ScrollView, View } from 'react-native';
import type { TextInput } from 'react-native';

import { login, register } from '@/api/auth/auth';
import { ErrorBanner } from '@/components/screen-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Text } from '@/components/ui/text';
import { openSession } from '@/lib/auth';
import { fieldErrors } from '@/lib/problem';

type Mode = 'signIn' | 'register';

const copy = {
  signIn: {
    heading: 'signIn.heading',
    description: 'signIn.description',
    submit: 'signIn.submit',
    switch: 'signIn.switchToRegister',
  },
  register: {
    heading: 'signIn.registerHeading',
    description: 'signIn.registerDescription',
    submit: 'signIn.registerSubmit',
    switch: 'signIn.switchToSignIn',
  },
} as const;

/**
 * Giriş ve kayıt ekranı. Doğrulama backend'dedir: alan hataları yanıttaki `errors` içinden, öteki hatalar
 * (yanlış parola, alınmış e-posta) `detail` alanından okunur.
 */
export default function SignInScreen() {
  const { t } = useTranslation();
  const [mode, setMode] = React.useState<Mode>('signIn');
  const [email, setEmail] = React.useState('');
  const [password, setPassword] = React.useState('');
  const passwordInput = React.useRef<TextInput>(null);

  // Giriş ve oturumun açılması tek adımdır: token alınır, hesabın kimliği sorulur ve oturum saklanır.
  const submission = useMutation({
    mutationFn: async (credentials: { email: string; password: string }) => {
      const tokens =
        mode === 'register' ? await register(credentials) : await login(credentials);

      await openSession(tokens.data);
    },
  });

  const errors = fieldErrors(submission.error);
  const hasFieldErrors = Object.keys(errors).length > 0;
  const text = copy[mode];

  function submit() {
    if (!submission.isPending) {
      submission.mutate({ email: email.trim(), password });
    }
  }

  return (
    <KeyboardAvoidingView
      className="flex-1"
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <ScrollView
        contentContainerClassName="flex-grow justify-center p-4"
        keyboardShouldPersistTaps="handled">
        <Card className="w-full max-w-sm self-center">
          <CardHeader>
            <CardTitle>{t(text.heading)}</CardTitle>
            <CardDescription>{t(text.description)}</CardDescription>
          </CardHeader>
          <CardContent className="gap-4">
            {hasFieldErrors ? null : (
              <View className="-mx-4 -mt-3">
                <ErrorBanner error={submission.error} />
              </View>
            )}
            <View className="gap-1.5">
              <Label nativeID="email-label" htmlFor="email">
                {t('signIn.email')}
              </Label>
              <Input
                id="email"
                aria-labelledby="email-label"
                aria-invalid={errors.email !== undefined}
                value={email}
                onChangeText={setEmail}
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="email"
                keyboardType="email-address"
                textContentType="emailAddress"
                returnKeyType="next"
                onSubmitEditing={() => passwordInput.current?.focus()}
              />
              {errors.email?.map((message) => (
                <Text key={message} className="text-sm text-destructive" role="alert">
                  {message}
                </Text>
              ))}
            </View>
            <View className="gap-1.5">
              <Label nativeID="password-label" htmlFor="password">
                {t('signIn.password')}
              </Label>
              <Input
                ref={passwordInput}
                id="password"
                aria-labelledby="password-label"
                aria-invalid={errors.password !== undefined}
                value={password}
                onChangeText={setPassword}
                secureTextEntry
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete={mode === 'register' ? 'new-password' : 'current-password'}
                textContentType={mode === 'register' ? 'newPassword' : 'password'}
                returnKeyType="done"
                onSubmitEditing={submit}
              />
              {mode === 'register' ? (
                <Text className="text-sm text-muted-foreground">{t('signIn.passwordHelp')}</Text>
              ) : null}
              {errors.password?.map((message) => (
                <Text key={message} className="text-sm text-destructive" role="alert">
                  {message}
                </Text>
              ))}
            </View>
            <Button size="lg" onPress={submit} disabled={submission.isPending}>
              <Text>{submission.isPending ? t('signIn.submitting') : t(text.submit)}</Text>
            </Button>
            <Button
              variant="link"
              className="h-11"
              onPress={() => {
                setMode(mode === 'signIn' ? 'register' : 'signIn');
                submission.reset();
              }}>
              <Text>{t(text.switch)}</Text>
            </Button>
          </CardContent>
        </Card>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}